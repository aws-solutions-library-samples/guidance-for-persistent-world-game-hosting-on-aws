// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using UnityEngine;
using Aws.GameLift.Server;
using System;
using System.Collections.Generic;
using Amazon.DynamoDBv2;
using System.Threading.Tasks;
using Amazon.DynamoDBv2.Model;
using Amazon.SecurityToken.Model;

// *** MONOBEHAVIOUR TO MANAGE SERVER LOGIC *** //

public class Server : MonoBehaviour
{
    // These are picked automatically from the game session info that comes from the backend
    public static string fleetRoleArn = "";
    public static string worldsConfigTableName = "";
    public static string worldPlayerDataTable = "";

    public GameObject playerPrefab;

#if SERVER

    // This Region is the one the game server is running in and will be used to access Global tables locally in the region
    private Amazon.RegionEndpoint localRegion = Amazon.Util.EC2InstanceMetadata.Region;

    private GameLift gameLift;

    // List of players
    public List<NetworkPlayer> players = new List<NetworkPlayer>();
    public Dictionary<int, string> playerSessionIds = new Dictionary<int, string>();
    public Dictionary<int, string> playerCognitoIds = new Dictionary<int, string>();
    public int rollingPlayerId = 0; //Rolling player id that is used to give new players an ID when connecting

    //We get events back from the NetworkServer through this static list
    public static List<SimpleMessage> messagesToProcess = new List<SimpleMessage>();

    private NetworkServer server;

    //Timer for checking the termination information from DynamoDB
    private float timeSinceLastTerminationCheck = 0.0f;
    private float terminationCheckInterval = 5.0f;

    // Fleet role credentials
    private DateTime lastCredentialsSync = DateTime.MinValue;
    private int credentialsSyncInterval = 1800;
    Credentials fleetRoleCredentials = null;

    // Helper function to check if a player exists in the enemy list already
    private bool PlayerExists(int clientId)
    {
        foreach (NetworkPlayer player in players)
        {
            if (player.GetPlayerId() == clientId)
            {
                return true;
            }
        }
        return false;
    }

    // Helper function to find a player from the enemy list
    private NetworkPlayer GetPlayer(int clientId)
    {
        foreach (NetworkPlayer player in players)
        {
            if (player.GetPlayerId() == clientId)
            {
                return player;
            }
        }
        return null;
    }

    public void RemovePlayer(int clientId)
    {
        foreach (NetworkPlayer player in players)
        {
            if (player.GetPlayerId() == clientId)
            {
                try
                {
                    GameLiftServerAPI.RemovePlayerSession(player.playerSessionId);
                }
                catch (Exception e)
                {
                    System.Console.WriteLine("Couldn't remove player session for player: " + clientId);
                }

                // Asynchronously update player position data to DynamoDB for next entry to this world
                Task.Run(() => UpdatePlayerPositionData(player.playerCognitoId, player.GetTransform().position.x, player.GetTransform().position.y, player.GetTransform().position.z));

                // Delete the player
                player.DeleteGameObject();
                players.Remove(player);
                return;
            }
        }
    }

    public void SetPlayerSessionId(int clientId, string playerSessionId)
    {
        this.playerSessionIds.Add(clientId, playerSessionId);
    }

    public string GetPlayerSessionId(int clientId)
    {
        try
        {
            return this.playerSessionIds[clientId];
        }
        catch (Exception e)
        {
            System.Console.WriteLine("Couldn't find player session ID for player: " + clientId);
        }

        return "";
    }

    public void SetPlayerCognitoId(int clientId, string playerId)
    {
        this.playerCognitoIds.Add(clientId, playerId);
    }

    public string GetPlayerCognitoId(int clientId)
    {
        try
        {
            return this.playerCognitoIds[clientId];
        }
        catch (Exception e)
        {
            System.Console.WriteLine("Couldn't find player Cognito ID for player: " + clientId);
        }

        return null;
    }

    // Start is called before the first frame update
    void Start()
    {
        // Set the target framerate to 30 FPS to avoid running at 100% CPU. Clients send input at 20 FPS
        Application.targetFrameRate = 30;

        this.gameLift = GameObject.FindObjectOfType<GameLift>();
        server = new NetworkServer(gameLift, this);
    }

    // FixedUpdate is called 30 times per second (configured in Project Settings -> Time -> Fixed TimeStep).
    // This is the interval we're running the simulation and processing messages on the server
    void FixedUpdate()
    {
        // Update the Network server to check client status and get messages
        server.Update();

        // Process any messages we received
        this.ProcessMessages();

        // Move players based on latest input and update player states to clients
        for (int i = 0; i < this.players.Count; i++)
        {
            var player = this.players[i];
            // Move
            player.Move();

            // Send state if changed
            var positionMessage = player.GetPositionMessage();
            if (positionMessage != null)
            {
                positionMessage.clientId = player.GetPlayerId();
                this.server.TransmitMessage(positionMessage, player.GetPlayerId());
                //Send to the player him/herself
                positionMessage.messageType = MessageType.PositionOwn;
                this.server.SendMessage(player.GetPlayerId(), positionMessage);
            }
        }

        // Check if world termination is scheduled
        this.timeSinceLastTerminationCheck += Time.fixedDeltaTime;
        if (this.timeSinceLastTerminationCheck >= this.terminationCheckInterval)
        {
            System.Console.WriteLine("Checking if termination is requested");
            Task.Run(CheckForTerminationRequest);
            //System.Console.WriteLine("Check request sent.");
            this.timeSinceLastTerminationCheck = 0.0f;
        }
    }

    private async Task<Credentials> GetFleeRoleCredentials()
    {
        // Check if we need to refresh credentials
        if (this.fleetRoleCredentials == null || (DateTime.Now - this.lastCredentialsSync).TotalSeconds >= this.credentialsSyncInterval)
        {
            System.Console.WriteLine("Need to refresh Fleet Role Credentials");
            var stsclient = new Amazon.SecurityToken.AmazonSecurityTokenServiceClient(this.localRegion);

            // Get and display the information about the identity of the default user.
            var callerIdRequest = new GetCallerIdentityRequest();
            var caller = await stsclient.GetCallerIdentityAsync(callerIdRequest);
            System.Console.WriteLine($"Original Caller: {caller.Arn}");

            // Create the request to use with the AssumeRoleAsync call.
            var assumeRoleReq = new AssumeRoleRequest()
            {
                DurationSeconds = this.credentialsSyncInterval + 5, // We add a few extra seconds compared to the interval to make sure these stay valid during refresh
                RoleSessionName = "Session1",
                RoleArn = Server.fleetRoleArn
            };

            var assumeRoleRes = await stsclient.AssumeRoleAsync(assumeRoleReq);
            this.fleetRoleCredentials = assumeRoleRes.Credentials;

            // Reset the date
            this.lastCredentialsSync = DateTime.Now;
        }

        return this.fleetRoleCredentials;
    }

    // Async method to check for world termination information from DynamoDB
    private async Task CheckForTerminationRequest()
    {
        if (this.gameLift.gameSession == null || Server.fleetRoleArn == "" || Server.worldsConfigTableName == "")
        {
            System.Console.WriteLine("No game session yet, don't check the termination info");
            return;
        }

        // 1. GET CREDENTIALS
        var credentials = await this.GetFleeRoleCredentials();

        // 2. GET MY WORLD DATA
        //AmazonDynamoDBClient client = new AmazonDynamoDBClient(new Amazon.Runtime.SessionAWSCredentials("", "", ""), this.localRegion); // You can use something like this for local testing
        AmazonDynamoDBClient client = new AmazonDynamoDBClient(credentials: credentials, this.localRegion);

        Dictionary<string, AttributeValue> key = new Dictionary<string, AttributeValue>
        {
            { "Location", new AttributeValue { S =  Amazon.Util.EC2InstanceMetadata.Region.SystemName } },
            { "WorldID", new AttributeValue { S = this.gameLift.gameSession.Name } }
        };

        var response = await client.GetItemAsync(new Amazon.DynamoDBv2.Model.GetItemRequest(Server.worldsConfigTableName, key));

        // 3. CHECK IF WE HAVE A SCHEDULED TERMINATION. NOTE: In our case we just kill the session, you might want to do something more graceful like wait for players to leave
        if (response.Item.ContainsKey("TerminateSession") && response.Item["TerminateSession"].S == "YES")
        {
            System.Console.WriteLine("Session termination requested, end the session. ");
            this.gameLift.TerminateGameSession();
        }
    }

    // Async method to update player position data for this world
    private async Task UpdatePlayerPositionData(string playerCognitoId, float playerX, float playerY, float playerZ)
    {
        System.Console.WriteLine("Updating player data to DynamoDB for the next time they join.");

        // 1. GET CREDENTIALS
        var credentials = await this.GetFleeRoleCredentials();

        // 2. UPDATE THE PLAYER DATA
        //AmazonDynamoDBClient client = new AmazonDynamoDBClient(new Amazon.Runtime.SessionAWSCredentials("", "", ""), this.localRegion); // You can use something like this for local testing
        AmazonDynamoDBClient client = new AmazonDynamoDBClient(credentials: credentials, this.localRegion);

        Dictionary<string, AttributeValue> attributes = new Dictionary<string, AttributeValue>
        {
            { "Location_WorldID", new AttributeValue { S =  Amazon.Util.EC2InstanceMetadata.Region.SystemName + "_" + this.gameLift.gameSession.Name } },
            { "PlayerCognitoID", new AttributeValue { S =  playerCognitoId } },
            { "LastPosX", new AttributeValue { S = playerX.ToString() } },
            { "LastPosY", new AttributeValue { S = playerY.ToString() } },
            { "LastPosZ", new AttributeValue { S = playerZ.ToString() } }
        };

        var response = await client.PutItemAsync(new Amazon.DynamoDBv2.Model.PutItemRequest(Server.worldPlayerDataTable, attributes));

        System.Console.WriteLine("Got response for player data write: " + response.HttpStatusCode.ToString());

    }

    // Async method to get the previous location for a player in this world
    private async Task CheckForPreviousLocationForPlayer(NetworkPlayer player)
    {
        // 1. GET CREDENTIALS
        var credentials = await this.GetFleeRoleCredentials();

        // 2. GET THE PLAYER DATA IN THIS WORLD
        //AmazonDynamoDBClient client = new AmazonDynamoDBClient(new Amazon.Runtime.SessionAWSCredentials("", "", ""), this.localRegion); // You can use something like this for local testing
        AmazonDynamoDBClient client = new AmazonDynamoDBClient(credentials: credentials, this.localRegion);

        Dictionary<string, AttributeValue> key = new Dictionary<string, AttributeValue>
        {
            { "Location_WorldID", new AttributeValue { S =  Amazon.Util.EC2InstanceMetadata.Region.SystemName + "_" + this.gameLift.gameSession.Name } },
            { "PlayerCognitoID", new AttributeValue { S =  player.playerCognitoId } },
        };

        var response = await client.GetItemAsync(new Amazon.DynamoDBv2.Model.GetItemRequest(Server.worldPlayerDataTable, key));

        //Print out response
        Console.WriteLine("Player previous location response:");
        Dictionary<string, AttributeValue> item = response.Item;
        foreach (var keyValuePair in item)
        {
            Console.WriteLine("{0} : S={1}, N={2}",keyValuePair.Key,keyValuePair.Value.S, keyValuePair.Value.N);
        }

        // 3. IF WE GOT A RESPONSE, SET THE LOCATION AND INFORM THE PLAYER IT WAS SET

        if (response.Item.ContainsKey("LastPosX"))
        {
            float x = float.Parse(response.Item["LastPosX"].S);
            float y = float.Parse(response.Item["LastPosY"].S);
            float z = float.Parse(response.Item["LastPosZ"].S);
            // Force set server position
            player.SetPosition(x,y,z);
            // Inform player
            var message = new SimpleMessage(MessageType.LastPositionSet, "");
            message.SetFloats(x, y, z, 0.0f, 0.0f, 0.0f, 0.0f);
            this.server.SendMessage(player.GetPlayerId(), message);
        }
    }

    private void ProcessMessages()
    {
        // Go through any messages we received to process
        foreach (SimpleMessage msg in messagesToProcess)
        {
            // Spawn player
            if (msg.messageType == MessageType.Spawn)
            {
                Debug.Log("Player spawned: " + msg.float1 + "," + msg.float2 + "," + msg.float3);
                NetworkPlayer player = new NetworkPlayer(msg.clientId);
                this.players.Add(player);
                player.Spawn(msg, this.playerPrefab);
                player.SetPlayerId(msg.clientId); // rolling server client ID
                player.playerSessionId = this.GetPlayerSessionId(msg.clientId); // the GameLift player session ID
                player.playerCognitoId = this.GetPlayerCognitoId(msg.clientId); // The Cognito ID received through GameLift that uniquely identifies the player

                // Send all existing player positions to the newly joined
                for (int i = 0; i < this.players.Count-1; i++)
                {
                    var otherPlayer = this.players[i];
                    // Send state
                    var positionMessage = otherPlayer.GetPositionMessage(overrideChangedCheck: true);
                    if (positionMessage != null)
                    {
                        positionMessage.clientId = otherPlayer.GetPlayerId();
                        this.server.SendMessage(player.GetPlayerId(), positionMessage);
                    }
                }

                // Start asynchronously checking from the database if we have a previous position for this player we should warp to
                Task.Run(() => CheckForPreviousLocationForPlayer(player));
            }

            // Set player input
            if (msg.messageType == MessageType.PlayerInput)
            {
                // Only handle input if the player exists
                if (this.PlayerExists(msg.clientId))
                {
                    //Debug.Log("Player moved: " + msg.float1 + "," + msg.float2 + " ID: " + msg.clientId);

                    if (this.PlayerExists(msg.clientId))
                    {
                        var player = this.GetPlayer(msg.clientId);
                        player.SetInput(msg);
                    }
                    else
                    {
                        Debug.Log("PLAYER MOVED BUT IS NOT SPAWNED! SPAWN TO RANDOM POS");
                        Vector3 spawnPos = new Vector3(UnityEngine.Random.Range(-5, 5), 1, UnityEngine.Random.Range(-5, 5));
                        var quat = Quaternion.identity;
                        SimpleMessage tmpMsg = new SimpleMessage(MessageType.Spawn);
                        tmpMsg.SetFloats(spawnPos.x, spawnPos.y, spawnPos.z, quat.x, quat.y, quat.z, quat.w);
                        tmpMsg.clientId = msg.clientId;

                        NetworkPlayer player = new NetworkPlayer(msg.clientId);
                        this.players.Add(player);
                        player.Spawn(tmpMsg, this.playerPrefab);
                        player.SetPlayerId(msg.clientId);
                    }
                }
                else
                {
                    Debug.Log("Player doesn't exists anymore, don't take in input: " + msg.clientId);
                }
            }
        }
        messagesToProcess.Clear();
    }

    public void DisconnectAll()
    {
        this.server.DisconnectAll();
    }
#endif
}