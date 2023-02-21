// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System.Net;
using System.Net.Sockets;
using UnityEngine;
using Aws.GameLift.Server;
using System;
using System.Collections.Generic;

// *** SERVER NETWORK LOGIC *** //

public class NetworkServer
{

#if SERVER
    private TcpListener listener = null;
    // Clients are stored as a dictionary of the TCPCLient and the ClientID
    private Dictionary<TcpClient, int> clients = new Dictionary<TcpClient, int>();
    private List<TcpClient> clientsToRemove = new List<TcpClient>();

    private GameLift gamelift = null;
    private Server server = null;

    private float listenerRefreshCounter = 0.0f;

    public int GetPlayerCount() { return clients.Count; }

    public NetworkServer(GameLift gamelift, Server server)
    {
        this.server = server;
        this.gamelift = gamelift;
        this.StartListener();
    }

    private void StartListener()
    {
        if(listener != null)
            listener.Stop();
        //Start the TCP server
        int port = this.gamelift.listeningPort;
        Debug.Log("Starting server on port " + port);
        listener = new TcpListener(IPAddress.Any, this.gamelift.listeningPort);
        Debug.Log("Listening at: " + listener.LocalEndpoint.ToString());
        listener.Start();
    }

    // Checks if socket is still connected
    private bool IsSocketConnected(TcpClient client)
    {
        var bClosed = false;

        // Detect if client disconnected
        if (client.Client.Poll(0, SelectMode.SelectRead))
        {
            byte[] buff = new byte[1];
            if (client.Client.Receive(buff, SocketFlags.Peek) == 0)
            {
                // Client disconnected
                bClosed = true;
            }
        }

        return !bClosed;
    }

    private void CheckForListenerRefresh()
    {
        // When there are no clients, we refresh the TCPListener every 10 minutes as it has issues when running for hours
        if(this.clients.Count <= 0)
        {
            this.listenerRefreshCounter += Time.deltaTime;
            if(this.listenerRefreshCounter > 600)
            {
                this.StartListener();
                this.listenerRefreshCounter = 0.0f;
            }
        }
    }

    public void Update()
    {
        this.CheckForListenerRefresh();

        // Are there any new connections pending?
        if (listener.Pending())
        {
            System.Console.WriteLine("Client pending..");
            TcpClient client = listener.AcceptTcpClient();
            client.NoDelay = true; // Use No Delay to send small messages immediately. UDP should be used for even faster messaging
            System.Console.WriteLine("Client accepted.");

            // Only allow the maximum amount of players defined for this world type
            if (this.clients.Count < this.gamelift.maxPlayers)
            {
                // Add client and give it the Id of the value of rollingPlayerId
                this.clients.Add(client, this.server.rollingPlayerId);
                this.server.rollingPlayerId++;
                return;
            }
            else
            {
                // game already full, reject the connection
                try
                {
                    SimpleMessage message = new SimpleMessage(MessageType.Reject, "game already full");
                    NetworkProtocol.Send(client, message);
                }
                catch (SocketException) { }
            }

        }

        // Iterate through clients and check if they have new messages or are disconnected
        foreach (var client in this.clients)
        {
            var tcpClient = client.Key;
            try
            {
                if (tcpClient == null) continue;
                if (this.IsSocketConnected(tcpClient) == false)
                {
                    System.Console.WriteLine("Client not connected anymore");
                    this.clientsToRemove.Add(tcpClient);
                }
                var messages = NetworkProtocol.Receive(tcpClient);

                // If we receive a null response, it means the client sending incorrenct message format and we'll disconnect them immediately
                if(messages == null)
                {
                    System.Console.WriteLine("Client sending wrong message format, disconnect immediately.");
                    this.clientsToRemove.Add(tcpClient);
                }
                // Otherwise, iterate through messages received
                else
                {
                    foreach (SimpleMessage message in messages)
                    {
                        //System.Console.WriteLine("Received message: " + message.message + " type: " + message.messageType);
                        bool disconnect = HandleMessage(tcpClient, message);
                        if (disconnect)
                            this.clientsToRemove.Add(tcpClient);
                    }
                }
            }
            catch (Exception e)
            {
                System.Console.WriteLine("Error receiving from a client: " + e.Message);
                this.clientsToRemove.Add(tcpClient);
            }
        }

        //Remove dead clients
        foreach (var clientToRemove in this.clientsToRemove)
        {
            try
            {
                this.RemoveClient(clientToRemove);
            }
            catch (Exception e)
            {
                System.Console.WriteLine("Couldn't remove client: " + e.Message);
            }
        }
        this.clientsToRemove.Clear();

    }

    public void DisconnectAll()
    {
        // warn clients
        SimpleMessage message = new SimpleMessage(MessageType.Disconnect);
        TransmitMessage(message);
        // disconnect connections
        foreach (var client in this.clients)
        {
            this.clientsToRemove.Add(client.Key);
        }

        //Reset the client lists
        this.clients = new Dictionary<TcpClient, int>();
        this.server.players = new List<NetworkPlayer>();
    }

    public void TransmitMessage(SimpleMessage msg, int excludeClient)
    {
        // send the same message to all players
        foreach (var client in this.clients)
        {
            //Skip if this is the excluded client
            if (client.Value == excludeClient)
            {
                continue;
            }

            try
            {
                NetworkProtocol.Send(client.Key, msg);
            }
            catch (Exception e)
            {
                this.clientsToRemove.Add(client.Key);
            }
        }
    }

    //Transmit message to multiple clients
    public void TransmitMessage(SimpleMessage msg, TcpClient excludeClient = null)
    {
        // send the same message to all players
        foreach (var client in this.clients)
        {
            //Skip if this is the excluded client
            if (excludeClient != null && excludeClient == client.Key)
            {
                continue;
            }

            try
            {
                NetworkProtocol.Send(client.Key, msg);
            }
            catch (Exception e)
            {
                this.clientsToRemove.Add(client.Key);
            }
        }
    }

    private TcpClient SearchClient(int clientId)
    {
        foreach (var client in this.clients)
        {
            if (client.Value == clientId)
            {
                return client.Key;
            }
        }
        return null;
    }

    public void SendMessage(int clientId, SimpleMessage msg)
    {
        try
        {
            TcpClient client = this.SearchClient(clientId);
            SendMessage(client, msg);
        }
        catch (Exception e)
        {
            Console.WriteLine("Failed to send message to client: " + clientId);
        }
    }
    //Send message to single client
    private void SendMessage(TcpClient client, SimpleMessage msg)
    {
        try
        {
            NetworkProtocol.Send(client, msg);
        }
        catch (Exception e)
        {
            this.clientsToRemove.Add(client);
        }
    }

    private bool HandleMessage(TcpClient client, SimpleMessage msg)
    {
        int clientId = this.clients[client];
        // If we're getting a message other than Connect and the player ID doesn't exist yet, disconnect (not authenticated)
        if (msg.messageType != MessageType.Connect && this.server.GetPlayerCognitoId(clientId) == null)
        {
            System.Console.WriteLine("Client didn't send valid player session ID before other messages, disconnect");
            return true; // disconnect
        }

        // Normal message processing
        if (msg.messageType == MessageType.Connect)
        {
            HandleConnect(clientId, msg.message, client);
        }
        else if (msg.messageType == MessageType.Disconnect)
        {
            return true; // disconnect
        }
        else if (msg.messageType == MessageType.Spawn)
            HandleSpawn(client, msg);
        else if (msg.messageType == MessageType.PlayerInput)
            HandleMove(client, msg);

        return false;
    }

    private void HandleConnect(int clientId, string playerSessionId, TcpClient client)
    {
        // respond with the player id and the current state.
        //Connect player
        var outcome = GameLiftServerAPI.AcceptPlayerSession(playerSessionId);
        if (outcome.Success)
        {
            System.Console.WriteLine("Player session successfully validated.");
            this.server.SetPlayerSessionId(clientId, playerSessionId);

            // Get the player ID to retrieve data specific to this player
            var playerSessionRequest = new Aws.GameLift.Server.Model.DescribePlayerSessionsRequest();
            playerSessionRequest.PlayerSessionId = playerSessionId;
            var describePlayerSesssionResponse = GameLiftServerAPI.DescribePlayerSessions(playerSessionRequest);
            if (describePlayerSesssionResponse != null && describePlayerSesssionResponse.Result.PlayerSessions.Count > 0)
            {
                // Note: You could get any player data you've set to the session here!
                string playerId = describePlayerSesssionResponse.Result.PlayerSessions[0].PlayerId;

                System.Console.WriteLine("Got player ID: " + playerId + " for player session: " + playerSessionId);

                // Store the PlayerID/CognitoID for future use to access player specific data
                this.server.SetPlayerCognitoId(clientId, playerId);
            }
        }
        else
        {
            System.Console.WriteLine(":( PLAYER SESSION REJECTED. AcceptPlayerSession() returned " + outcome.Error.ToString());
            // We don't validate sessions in local testing
#if !LOCAL_GAME
            this.clientsToRemove.Add(client);
#endif
        }
    }

    private void HandleSpawn(TcpClient client, SimpleMessage message)
    {
        // Get client id (this is the value in the dictionary where the TCPClient is the key)
        int clientId = this.clients[client];

        System.Console.WriteLine("Player " + clientId + " spawned with coordinates: " + message.float1 + "," + message.float2 + "," + message.float3);

        // Add client ID
        message.clientId = clientId;

        // Add to list to create the gameobject instance on the server
        Server.messagesToProcess.Add(message);

        // Just testing the StatsD client
        this.gamelift.GetStatsdClient().SendCounter("players.PlayerSpawn", 1);
    }

    private void HandleMove(TcpClient client, SimpleMessage message)
    {
        // Get client id (this is the value in the dictionary where the TCPClient is the key)
        int clientId = this.clients[client];

        //System.Console.WriteLine("Got move from client: " + clientId + " with input: " + message.float1 + "," + message.float2);

        // Add client ID
        message.clientId = clientId;

        // Add to list to create the gameobject instance on the server
        Server.messagesToProcess.Add(message);
    }

    private void RemoveClient(TcpClient client)
    {
        // TODO: Handle situation where this fails (the client is probably null? Or not added at all? The server still would have the NetworkPlayer which is left hanging)
        //Let the other clients know the player was removed
        int clientId = this.clients[client];

        try
        {
            SimpleMessage message = new SimpleMessage(MessageType.PlayerLeft);
            message.clientId = clientId;
            TransmitMessage(message, client);
        }
        catch (Exception e)
        {
            System.Console.WriteLine("Couldn't inform other players about removing client but removing anyways. exception: " + e.Message);
        }


        // Disconnect and remove
        try
        {
            this.clients.Remove(client);
        }
        catch (Exception e)
        {
            System.Console.WriteLine("Error removing client from clients dictionary: " + e.Message);
        }
        this.DisconnectPlayer(client);
        this.server.RemovePlayer(clientId);
    }

    private void DisconnectPlayer(TcpClient client)
    {
        try
        {
            // remove the client and close the connection
            if (client != null)
            {
                NetworkStream stream = client.GetStream();
                stream.Close();
                client.Close();
            }
        }
        catch (Exception e)
        {
            System.Console.WriteLine("Failed to disconnect player: " + e.Message);
        }
    }

#endif
}
