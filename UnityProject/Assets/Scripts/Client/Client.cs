// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using UnityEngine;
using System.Collections.Generic;
using System.Collections;
using Amazon.CognitoIdentity;
using System.Net.Http;
using System.Threading.Tasks;
using System;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using Amazon.CognitoIdentity.Model;

// *** MAIN CLIENT CLASS FOR MANAGING CLIENT CONNECTIONS AND MESSAGES ***

public class Client : MonoBehaviour
{
    /// *** MAIN MENU AND BACKEND INTEGRATIONS *** ///

    private UIManager uiManager;

    // Worlds for the current requested location
    private WorldsData worldsData = null;

    //Cognito credentials for sending signed requests to the API
    public static Amazon.Runtime.ImmutableCredentials cognitoCredentials = null;
    public static string cognitoID = null;

    private BackendApiClient backendApiClient;

    // NOTE: DON'T EDIT THESE HERE, as they are overwritten by values in the Client GameObject. Set in Inspector instead
    public string apiEndpoint = "https://<YOUR-API-ENDPOINT./Prod/";
    public string identityPoolID = "<YOUR-IDENTITY-POOL-ID>";
    public string regionString = "us-east-1";
    public Amazon.RegionEndpoint region = Amazon.RegionEndpoint.USEast1; // This will be automatically set based on regionString

    /// *** GAME WORLD STATE MANAGEMENT *** ///
    
    // Prefabs for the player and enemy objects referenced from the scene object
    public GameObject characterPrefab;
    public GameObject enemyPrefab;

    // Local player
    private NetworkClient networkClient;
    private NetworkPlayer localPlayer;

    private float updateCounter = 0.0f;

    //We get events back from the NetworkServer through this static list
    public static List<SimpleMessage> messagesToProcess = new List<SimpleMessage>();

    // List of other players
    private List<NetworkPlayer> otherPlayers = new List<NetworkPlayer>();

    /// *** FOR BOTS ONLY *** ///

#if BOTCLIENT
    private int botMovementChangeCount = 0;
    private float currentBotMovementX = 0;
    private float currentBotMovementZ = 0;
    private float botSessionTimer = 60.0f; // seconds value for running a bot session before restarting
#endif

#if CLIENT

    /// *** MAIN MENU AND BACKEND INTEGRATIONS *** ///

    void ConnectToCognito()
    {
        // Check if we have stored an identity and request credentials for that existing identity
        Client.cognitoID = PlayerPrefs.GetString("CognitoID", null);
        if (Client.cognitoID != null && Client.cognitoID != "")
        {
            Debug.Log("Requesting credentials for existing identity: " + Client.cognitoID);
            var response = Task.Run(() => GetCredentialsForExistingIdentity(Client.cognitoID));
            response.Wait(5000);
            Client.cognitoID = response.Result.IdentityId;
            Client.cognitoCredentials = new Amazon.Runtime.ImmutableCredentials(response.Result.Credentials.AccessKeyId, response.Result.Credentials.SecretKey, response.Result.Credentials.SessionToken);
        }
        // Else get a new identity
        else
        {
            Debug.Log("Requesting a new playeridentity as none stored yet.");
            CognitoAWSCredentials credentials = new CognitoAWSCredentials(
                this.identityPoolID,
                this.region);
            Client.cognitoCredentials = credentials.GetCredentials();
            Client.cognitoID = credentials.GetIdentityId();
            Debug.Log("Got Cognito ID: " + credentials.GetIdentityId());

            // Store to player prefs and save for future games
            PlayerPrefs.SetString("CognitoID", Client.cognitoID);
            PlayerPrefs.Save();
        }
    }

    // Retrieves credentials for existing identities
    async Task<GetCredentialsForIdentityResponse> GetCredentialsForExistingIdentity(string identity)
    {
        // As this is a public API, we call it with fake access keys
        AmazonCognitoIdentityClient cognitoClient = new AmazonCognitoIdentityClient("A", "B", this.region);
        var resp = await cognitoClient.GetCredentialsForIdentityAsync(identity);
        return resp;
    }

    void ListWorlds()
    {
        WorldsData worlds = this.backendApiClient.RequestListWorlds(this.uiManager.regionDropdown.options[this.uiManager.regionDropdown.value].text);

        // Store for joining
        this.worldsData = worlds;

        this.uiManager.SetInfoTextBox("Received worlds data.");

        // Delete any old worlds from scroll list
        foreach (Transform child in this.uiManager.worldInfoContentContainer)
            GameObject.Destroy(child.gameObject);

        // Add requested worlds
        int index = 0;
        foreach (var world in worlds.Worlds)
        {
            Debug.Log("World:" + world.WorldID);
            var item = this.uiManager.AddWorldItemToList(world);
            // add a listener for the join button
            var i2 = index;
            item.GetComponentInChildren<Button>().onClick.AddListener(() => JoinWorld(i2));
            index++;
        }
    }

    void JoinWorld(int index)
    {
        this.uiManager.SetInfoTextBox("Joining world...");

        // Delete any old worlds from scroll list to avoid any additional join clicks
        foreach (Transform child in this.uiManager.worldInfoContentContainer)
            GameObject.Destroy(child.gameObject);

        Debug.Log(index);
        var worldToJoin = this.worldsData.Worlds[index];

        Debug.Log("Joining world: " + worldToJoin.WorldID + " Location: " + worldToJoin.Location);
        this.uiManager.SetInfoSubTextBox("Joining world: " + worldToJoin.WorldID + " Location: " + worldToJoin.Location);

        GameSessionInfo gameSession = this.backendApiClient.RequestJoinWorld(worldToJoin.Location, worldToJoin.WorldID);

        //TODO: Handle errors!

        Debug.Log("Got player session: " + gameSession.PlayerSessionId + ", " + gameSession.IpAddress + ":" + gameSession.Port);
        this.uiManager.SetInfoSubTextBox("Got player session: " + gameSession.PlayerSessionId + ", " + gameSession.IpAddress + ":" + gameSession.Port);

        //Load the correct world locally
        SceneManager.LoadScene(worldToJoin.WorldMap, LoadSceneMode.Additive);

        //Hide all the unnecessary UI components
        uiManager.HideMainMenuUI();

        // Connect to the server now that we have our identity, credendtials and latencies
        StartCoroutine(ConnectToServer(gameSession));
    }

    // Called when restart button is clicked
    public void Restart()
    {
        this.networkClient.Disconnect();
        SceneManager.LoadScene(0);
    }

    // Called by Unity when the Gameobject is created
    void Start()
    {
        this.uiManager = FindObjectOfType<UIManager>();
        this.backendApiClient = new BackendApiClient();

        // Get the Region enum from the string value
        this.region = Amazon.RegionEndpoint.GetBySystemName(regionString);
        Debug.Log("My Region endpoint: " + this.region);

        // Connect to Cognito first
        ConnectToCognito();

        // Add listeners for UI buttons
        this.uiManager.listWorldsButton.onClick.AddListener(ListWorlds);
        this.uiManager.restartButton.onClick.AddListener(Restart);

#if BOTCLIENT
        // Bots will start automatically
        // TODO: Update to ListWorlds and JoinWorld with random location and world
        System.Console.WriteLine("BOT: Start connecting immediately");
        this.StartGame();
#endif
    }


    ///
    /// *** GAME WORLD STATE MANAGEMENT *** ///
    /// 

    // Helper function check if an enemy exists in the enemy list already
    private bool EnemyPlayerExists(int clientId)
    {
        foreach(NetworkPlayer player in otherPlayers)
        {
            if(player.GetPlayerId() == clientId)
            {
                return true;
            }
        }
        return false;
    }

    // Helper function to find and enemy from the enemy list
    private NetworkPlayer GetEnemyPlayer(int clientId)
    {
        foreach (NetworkPlayer player in otherPlayers)
        {
            if (player.GetPlayerId() == clientId)
            {
                return player;
            }
        }
        return null;
    }

    // Update is called once per frame
    void Update()
    {
        if (this.localPlayer != null)
        {
#if BOTCLIENT
            this.BotUpdate();
#endif

            // Process any messages we have received over the network
            this.ProcessMessages();

            // Only send updates 20 times per second to avoid flooding server with messages
            this.updateCounter += Time.deltaTime;
            if (updateCounter < 0.05f)
            {
                return;
            }
            this.updateCounter = 0.0f;

            // Send current move command for server to process
            this.SendMove();

            // Receive new messages
            this.networkClient.Update();
        }
    }

    private void BotUpdate()
    {
#if BOTCLIENT
        this.botSessionTimer -= Time.deltaTime;
        if (this.botSessionTimer <= 0.0f)
        {
            System.Console.WriteLine("BOT: Restarting session.");
            this.Restart();
        }
#endif
    }

    // This is a coroutine to simplify the logic and keep our UI updated throughout the process
    IEnumerator ConnectToServer(GameSessionInfo gameSession)
    {
        
        uiManager.SetInfoTextBox("Connecting to backend..");

        yield return null;

        // Start network client and connect to server
        this.networkClient = new NetworkClient();
        // We will wait for the connection coroutine to end before creating the player
        this.networkClient.Connect(gameSession);

        if (this.networkClient.ConnectionSucceeded())
        {
            // Create character
            this.localPlayer = new NetworkPlayer(0);
            this.localPlayer.Initialize(characterPrefab, new Vector3(UnityEngine.Random.Range(50,60), 8, UnityEngine.Random.Range(30, 40)));
            this.localPlayer.ResetTarget();
            this.networkClient.SendMessage(this.localPlayer.GetSpawnMessage());
        }
        
        yield return null;
    }

    // Process messages received from server
    void ProcessMessages()
    {
        List<int> justLeftClients = new List<int>();
        List<int> clientsMoved = new List<int>();

        // Go through any messages to process
        foreach (SimpleMessage msg in messagesToProcess)
        {
            // Own position
            if (msg.messageType == MessageType.PositionOwn)
            {
                this.localPlayer.ReceivePosition(msg, this.characterPrefab);
                this.uiManager.SetInfoSubTextBox("Received player pos from server: " + msg.float1 + "," + msg.float2 + "," + msg.float3);
            }
            // players spawn and position messages
            else if (msg.messageType == MessageType.Spawn || msg.messageType == MessageType.Position || msg.messageType == MessageType.PlayerLeft)
            {
                if (msg.messageType == MessageType.Spawn && this.EnemyPlayerExists(msg.clientId) == false)
                {
                    //Debug.Log("Enemy spawned: " + msg.float1 + "," + msg.float2 + "," + msg.float3 + " ID: " + msg.clientId);
                    NetworkPlayer enemyPlayer = new NetworkPlayer(msg.clientId);
                    this.otherPlayers.Add(enemyPlayer);
                    enemyPlayer.Spawn(msg, this.enemyPrefab);
                }
                else if (msg.messageType == MessageType.Position && justLeftClients.Contains(msg.clientId) == false)
                {
                    //Debug.Log("Enemy pos received: " + msg.float1 + "," + msg.float2 + "," + msg.float3);
                    //Setup enemycharacter if not done yet
                    if (this.EnemyPlayerExists(msg.clientId) == false)
                    {
                        Debug.Log("Creating new enemy with ID: " + msg.clientId);
                        NetworkPlayer newPlayer = new NetworkPlayer(msg.clientId);
                        this.otherPlayers.Add(newPlayer);
                        newPlayer.Spawn(msg, this.enemyPrefab);
                    }
                    // We pass the prefab with the position message as it might be the enemy is not spawned yet
                    NetworkPlayer enemyPlayer = this.GetEnemyPlayer(msg.clientId);
                    enemyPlayer.ReceivePosition(msg, this.enemyPrefab);

                    clientsMoved.Add(msg.clientId);
                }
                else if (msg.messageType == MessageType.PlayerLeft)
                {
                    Debug.Log("Player left " + msg.clientId);
                    // A player left, remove from list and delete gameobject
                    NetworkPlayer enemyPlayer = this.GetEnemyPlayer(msg.clientId);
                    if (enemyPlayer != null)
                    {
                        //Debug.Log("Found enemy player");
                        enemyPlayer.DeleteGameObject();
                        this.otherPlayers.Remove(enemyPlayer);
                        justLeftClients.Add(msg.clientId);
                    }
                }
            }
            // player previous session location restored
            else if (msg.messageType == MessageType.LastPositionSet)
            {
                GameObject.FindObjectOfType<UIManager>().SetInfoTextBox("Your previous location was restored!");
                this.localPlayer.SetPosition(msg.float1, msg.float2, msg.float3);
            }
        }
        messagesToProcess.Clear();

        // Interpolate all enemy players towards their current target
        foreach (var enemyPlayer in this.otherPlayers)
        {
            enemyPlayer.InterpolateToTarget();
        }

        // Interpolate player towards his/her current target
        this.localPlayer.InterpolateToTarget();
    }

    void SendMove()
    {
        // Get movement input
        var newPosMessage = this.localPlayer.GetMoveMessage();

        // Bots will have randomized movement that slowly changes
#if BOTCLIENT
        if(this.botMovementChangeCount <= 0)
        {
            this.currentBotMovementX = UnityEngine.Random.Range(-1.0f, 1.0f);
            this.currentBotMovementZ = UnityEngine.Random.Range(-1.0f, 1.0f);
            this.botMovementChangeCount = 30;
        }
        this.botMovementChangeCount -= 1;

        newPosMessage.float1 = this.currentBotMovementX;
        newPosMessage.float2 = this.currentBotMovementZ;
#endif

        // Send if not null
        if (newPosMessage != null)
            this.networkClient.SendMessage(newPosMessage);
    }

#endif

}