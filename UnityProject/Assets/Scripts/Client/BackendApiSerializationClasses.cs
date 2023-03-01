// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0


//** SERIALIZATION OBJECTS FOR THE BACKEND API ***
[System.Serializable]
public class WorldsData
{
    [System.Serializable]
    public class WorldData
    {
        public string GameSessionId;
        public string Location;
        public int MaxPlayers;
        public int CurrentPlayerSessionCount;
        public string WorldMap;
        public string WorldID;
        public string DynamicWorld;
    }

    public WorldData[] Worlds;
}
[System.Serializable]
public class GameSessionInfo
{
    public string PlayerSessionId;
    public string PlayerId;
    public string GameSessionId;
    public string FleetId;
    public string CreationTime;
    public string Status;
    public string IpAddress;
    public int Port;
}