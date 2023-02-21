// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using System.Net.Sockets;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;

// *** NETWORK CLIENT FOR TCP CONNECTIONS WITH THE SERVER ***

public class NetworkClient
{
#if CLIENT
    private BackendApiClient backendApiClient;

	private TcpClient client = null;

	private bool connectionSucceeded = false;
	public bool ConnectionSucceeded() { return connectionSucceeded; }

    public NetworkClient()
    {
        this.backendApiClient = new BackendApiClient();
    }

	// Called by the client to receive new messages
	public void Update()
	{
		if (client == null) return;
		var messages = NetworkProtocol.Receive(client);
		if(messages != null)
		{
			foreach (SimpleMessage msg in messages)
			{
				HandleMessage(msg);
			}
		}
	}

	private bool TryConnect(GameSessionInfo gameSession)
	{
		try
		{
			//Connect with matchmaking info
			Debug.Log("Connect..");
			this.client = new TcpClient();
#if LOCAL_GAME
			var result = client.BeginConnect("127.0.0.1", 1935, null, null);
#else
			var result = client.BeginConnect(gameSession.IpAddress, gameSession.Port, null, null);
#endif

			var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));

			if (!success)
			{
				throw new Exception("Failed to connect.");
			}
			client.NoDelay = true; // Use No Delay to send small messages immediately. UDP should be used for even faster messaging
			Debug.Log("Done");

			// Send the player session ID to server so it can validate the player
            SimpleMessage connectMessage = new SimpleMessage(MessageType.Connect, gameSession.PlayerSessionId);
            this.SendMessage(connectMessage);

			return true;
		}
		catch (Exception e)
		{
			Debug.Log(e.Message);
			client = null;
			GameObject.FindObjectOfType<UIManager>().SetInfoTextBox("Failed to connect: " + e.Message);
			return false;
		}
	}

	public void Connect(GameSessionInfo gameSession)
	{
		// try to connect to a local server
		if (TryConnect(gameSession) == false)
		{
			Debug.Log("Failed to connect to server");
			GameObject.FindObjectOfType<UIManager>().SetInfoTextBox("Connection to server failed.");

			// Restart the client
			var clientObject = GameObject.FindObjectOfType<Client>();
			clientObject.Restart();
		}
		else
		{
			//We're ready to play, let the server know
			this.connectionSucceeded = true;
			GameObject.FindObjectOfType<UIManager>().SetInfoTextBox("Connected to server");
		}
	}

    // Send serialized binary message to server
    public void SendMessage(SimpleMessage message)
    {
        if (client == null) return;
        try
        {
            NetworkProtocol.Send(client, message);
        }
        catch (SocketException e)
        {
            HandleDisconnect();
        }
    }

	// Send disconnect message to server
	public void Disconnect()
	{
		if (client == null) return;
        SimpleMessage message = new SimpleMessage(MessageType.Disconnect);
		try
		{
			NetworkProtocol.Send(client, message);
		}

		finally
		{
			HandleDisconnect();
		}
	}

	// Handle a message received from the server
	private void HandleMessage(SimpleMessage msg)
	{
		// parse message and pass json string to relevant handler for deserialization
		//Debug.Log("Message received:" + msg.messageType + ":" + msg.message);
		if (msg.messageType == MessageType.Reject)
			HandleReject();
		else if (msg.messageType == MessageType.Disconnect)
			HandleDisconnect();
		else if (msg.messageType == MessageType.Spawn)
			HandleOtherPlayerSpawned(msg);
		else if (msg.messageType == MessageType.Position)
			HandleOtherPlayerPos(msg);
		else if (msg.messageType == MessageType.PositionOwn)
			HandlePlayerPos(msg);
		else if (msg.messageType == MessageType.PlayerLeft)
			HandleOtherPlayerLeft(msg);
		else if (msg.messageType == MessageType.LastPositionSet)
			HandleLastPositionSet(msg);
	}

    private void HandleLastPositionSet(SimpleMessage msg)
    {
		Client.messagesToProcess.Add(msg);
	}

    private void HandleReject()
	{
		NetworkStream stream = client.GetStream();
		stream.Close();
		client.Close();
		client = null;
	}

	private void HandleDisconnect()
	{
		try
		{
			Debug.Log("Got disconnected by server");
			GameObject.FindObjectOfType<UIManager>().SetInfoTextBox("Got disconnected by server");
			NetworkStream stream = client.GetStream();
			stream.Close();
			client.Close();
			client = null;
		}
		catch (Exception e)
		{
			Debug.Log("Error when disconnecting, setting client to null.");
			client = null;
		}
	}

	private void HandleOtherPlayerSpawned(SimpleMessage message)
	{
		Client.messagesToProcess.Add(message);
	}

	private void HandlePlayerPos(SimpleMessage message)
	{
		Client.messagesToProcess.Add(message);
	}

	private void HandleOtherPlayerPos(SimpleMessage message)
    {
		Client.messagesToProcess.Add(message);
	}

	private void HandleOtherPlayerLeft(SimpleMessage message)
	{
		Client.messagesToProcess.Add(message);
	}
#endif
}

