// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

// GameLift server configuration and callbacks

using System;
using UnityEngine;
using Aws.GameLift.Server;
using System.Collections.Generic;
using Aws.GameLift.Server.Model;
using UnityEngine.SceneManagement;

public class GameLift : MonoBehaviour
{
#if SERVER

    //Set the port that your game service is listening on for incoming player connections
    public int listeningPort = -1;

    // Game preparation state
    private bool gameSessionInfoReceived = false;

    // Game state
    public GameSession gameSession = null;
    private bool startGameSession = false;
    private string gameSessionId;
    public string GetGameSessionID() { return gameSessionId; }

    // StatsD client for sending custom metrics to CloudWatch through the local StatsD agent
    private SimpleStatsdClient statsdClient;
    public SimpleStatsdClient GetStatsdClient() { return statsdClient; }

    // Backfill ticket ID (received and updated on game session updates)
    string backfillTicketID = null;

    // Game session timer, we don't want to run over 20 minutes
    private float gameSessionTimer = 0.0f;

    // Max players
    public int maxPlayers;

    // Refresh counter: We refresh the game servers every 24 hours
    DateTime launchTime = DateTime.Now;

    // Get the port to host the server from the command line arguments
    private int GetPortFromArgs()
    {
        int defaultPort = 1935;
        int port = defaultPort; //Use default is arg not provided

        string[] args = System.Environment.GetCommandLineArgs();

        for (int i = 0; i < args.Length; i++)
        {
            Debug.Log("ARG " + i + ": " + args[i]);
            if (args[i] == "-port")
            {
                port = int.Parse(args[i + 1]);
            }
        }

        return port;
    }

    // Called when the monobehaviour is created
    public void Awake()
    {
        //Initiate the simple statsD client
        this.statsdClient = new SimpleStatsdClient("localhost", 8125);

        //Get the port from command line args
        listeningPort = this.GetPortFromArgs();

        System.Console.WriteLine("Will be running in port: " + this.listeningPort);

        //InitSDK establishes a local connection with the Amazon GameLift agent to enable 
        //further communication.
        var initSDKOutcome = GameLiftServerAPI.InitSDK();
        if (initSDKOutcome.Success)
        {
            ProcessParameters processParameters = new ProcessParameters(
                (gameSession) => {
                    //Respond to new game session activation request. GameLift sends activation request 
                    //to the game server along with a game session object containing game properties 
                    //and other settings.

                    //We'll do the activation in the next Update to do it from the main thread (required to load the worlds scene
                    this.gameSession = gameSession;
                    this.startGameSession = true;

                },
                (gameSession) => {
                    //Respond to game session updates (We don't get any in this setup)
                },
                () => {
                    //OnProcessTerminate callback. GameLift invokes this callback before shutting down 
                    //an instance hosting this game server. It gives this game server a chance to save
                    //its state, communicate with services, etc., before being shut down. 
                    //In this case, we simply tell GameLift we are indeed going to shut down.
                    GameLiftServerAPI.ProcessEnding();
                    Application.Quit();
                },
                () => {
                    //This is the HealthCheck callback.
                    //GameLift invokes this callback every 60 seconds or so.
                    return true;
                },
                //Here, the game server tells GameLift what port it is listening on for incoming player 
                //connections. We will use the port received from command line arguments
                listeningPort,
                new LogParameters(new List<string>()
                {
                    //Let GameLift know where our logs are stored. We are expecting the command line args to specify the server with the port in log file
                    "/local/game/logs/myserver"+listeningPort+".log"
                }));

            //Calling ProcessReady tells GameLift this game server is ready to receive incoming game sessions
            var processReadyOutcome = GameLiftServerAPI.ProcessReady(processParameters);

            if (processReadyOutcome.Success)
            {
                print("ProcessReady success.");
            }
            else
            {
                print("ProcessReady failure : " + processReadyOutcome.Error.ToString());
            }
        }
        else
        {
            print("InitSDK failure : " + initSDKOutcome.Error.ToString());
        }
    }

    // Ends the game session for all and disconnects the players
    public void TerminateGameSession()
    {
        System.Console.WriteLine("Terminating Game Session");

        //Cleanup (not currently relevant as we just terminate the process)
        GameObject.FindObjectOfType<Server>().DisconnectAll();

        // Terminate the process following GameLift best practices. A new one will be started automatically
        System.Console.WriteLine("Terminating process");
        GameLiftServerAPI.ProcessEnding();
        Application.Quit();
    }

    private bool CheckForServerRefresh()
    {
        if((DateTime.Now - this.launchTime).TotalHours >= 24)
            return true;
        
        return false;
    }

    // Called by Unity once a frame
    public void Update()
    {
        // We recycle the game server every 24 hours. You might want to do something more sophisticated in your own implementation, but it's recommended to have a mechanism to replace servers.
        // Our World Manager will automatically spin up a replacement
        if(this.CheckForServerRefresh())
        {
            Debug.Log("REFRESH: Time to refresh the session, closing down the world. World Manager will spin up a replacement.");
            this.TerminateGameSession();
            return;
        }

        //Initialize game session if requested
        if (this.startGameSession)
        {
            this.startGameSession = false;

            // Get the world properties for map loading and DynamoDB access
            string worldMap = "";
            foreach(var gameProperty in this.gameSession.GameProperties)
            {
                if (gameProperty.Key == "WorldMap")
                    worldMap = gameProperty.Value;
                else if(gameProperty.Key == "FleetRoleArn")
                    Server.fleetRoleArn = gameProperty.Value;
                else if(gameProperty.Key == "WorldConfigTable")
                    Server.worldsConfigTableName = gameProperty.Value;
                else if(gameProperty.Key == "WorldPlayerDataTable")
                    Server.worldPlayerDataTable = gameProperty.Value;
                else if(gameProperty.Key == "DynamicWorld")
                    Server.dynamicWorld = gameProperty.Value;
            }

            System.Console.WriteLine("Got Fleet Role Arn: " + Server.fleetRoleArn + " and Table: " + Server.worldsConfigTableName);

            //Additive Load the correct game world based on game properties
            Debug.Log("Loading world from game properties: " + this.gameSession.GameProperties[0].Key + ": " + gameSession.GameProperties[0].Value);
            SceneManager.LoadScene(worldMap, LoadSceneMode.Additive);
            Debug.Log("World loading done!");

            //Start waiting for players
            this.gameSessionInfoReceived = true;
            this.gameSessionId = this.gameSession.GameSessionId;

            //Set the max players from the game session attributes
            this.maxPlayers = this.gameSession.MaximumPlayerSessionCount;

            //Set the game session tag (CloudWatch dimension) for custom metrics
            string justSessionId = this.gameSessionId.Split('/')[2];
            this.statsdClient.SetCommonTagString("#gamesession:" + justSessionId);

            //Send session started to CloudWatch just for testing
            this.statsdClient.SendCounter("game.SessionStarted", 1);

            //Log the session ID
            System.Console.WriteLine("Game Session ID: " + justSessionId);

            // Activate the session
            GameLiftServerAPI.ActivateGameSession();
        }
    }

    void OnApplicationQuit()
    {
        // Terminate GameLift connection properly
        GameLiftServerAPI.ProcessEnding();
        GameLiftServerAPI.Destroy();
    }
#endif
}
