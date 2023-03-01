""" Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved. """
""" SPDX-License-Identifier: MIT-0 """

import json
import boto3
import botocore
import os
from datetime import datetime
import time

dynamodb = boto3.resource('dynamodb')
gamelift = boto3.client('gamelift')
    
""" Scans a DynamoDB table with pagination and returns the items in an array """
def scan_worlds(table_name):

    table = dynamodb.Table(table_name)

    done_scanning = False
    worlds_scanned = []
    scan_args = {}

    while not done_scanning:
        try:
            response = table.scan(**scan_args)
        except botocore.exceptions.ClientError as error:
            raise Exception('Error scanning DynamoDB: {}'.format(error))

        worlds_scanned.extend(response.get('Items', []))
        next_key = response.get('LastEvaluatedKey')
        scan_args['ExclusiveStartKey'] = next_key

        if next_key is None:
            done_scanning = True 

    return worlds_scanned

""" Checks if a world is properly deployed and running"""
def is_world_deployed(world_session, world_config):

    print("World " + world_config["WorldID"] + " has a world session already. Check if it's started correctly.")
    world_session_creation_time = datetime.fromtimestamp(int(world_session["CreationTime"]))
    last_updated_time = datetime.fromtimestamp(int(world_session["LastUpdatedTime"]))
    time_since_creation_seconds = (datetime.utcnow() - world_session_creation_time).total_seconds()
    time_since_last_update = (datetime.utcnow() - last_updated_time).total_seconds()
    # If we deployed 5 minutes ago but the world is not showing up, we can expect the game session is dead or not started
    if time_since_creation_seconds > 300 and world_session["Status"] == "PROVISIONING":
        print("World " + world_config["WorldID"] + " didn't start in expected time (5 minutes). Redeploying")
        return False
    # If the game session hasn't been found in the game sessions list for five minutes, redeploy
    elif time_since_last_update > 300:
        print("World " + world_config["WorldID"] + " is not found in game sessions anymore. Redeploying")
        return False
    # If the game session in error state, redeploy. NOTE: No mechanism currently used to terminate error sessions
    elif world_session["Status"] == "ERROR":
        print("World " + world_config["WorldID"] + " is in ERROR state. Redeploy. NOTE: The error state session will remain!")
        return False
    else:
        print("World session " + world_config["WorldID"] + " running, no need to deploy.")
        return True

""" Deploys a world from world config """
def deploy_world(world_config):

    # Provision the session on GameLift
    try:
        response = gamelift.create_game_session(
            AliasId=os.environ['FLEET_ALIAS'],
            MaximumPlayerSessionCount=int(world_config["MaxPlayers"]),
            Name=world_config["WorldID"],
            # We pass on the world map as well as relevant resources to the game server
            GameProperties=[
                {
                    'Key': 'WorldMap',
                    'Value': world_config["WorldMap"]
                },
                {
                    'Key': 'FleetRoleArn',
                    'Value': os.environ['FLEET_ROLE_ARN'] 
                },
                {
                    'Key': 'WorldConfigTable',
                    'Value': os.environ['WORLD_CONFIGURATIONS_TABLE']
                },
                {
                    'Key': 'WorldPlayerDataTable',
                    'Value': os.environ['WORLD_PLAYER_DATA_TABLE']
                }
            ],
            Location=world_config["Location"]
        )
    except botocore.exceptions.ClientError as error:
        print('Error creating world game session: {}'.format(error))
        print('Cancelling deployment of this world...')
        return

    dynamic_world = "NO"
    if "DynamicWorld" in world_config.keys():
        dynamic_world = world_config["DynamicWorld"]

    # Update on DynamoDB
    try:
        table = dynamodb.Table(os.environ['WORLD_SESSIONS_TABLE'])
        response = table.put_item(
            Item={
                'Location': world_config["Location"],
                'WorldID': world_config["WorldID"],
                'Status': "PROVISIONING",
                'CreationTime': int(round(datetime.utcnow().timestamp())),
                'MaxPlayers': world_config["MaxPlayers"],
                'WorldMap': world_config["WorldMap"],
                'DynamicWorld': dynamic_world
            }
        )
    except botocore.exceptions.ClientError as error:
        raise Exception('Error writing new world data to DynamoDB (WILL RESULT IN MULTIPLE WORLDS DEPLOYED!): {}'.format(error))

""" Goes through all the worlds and updates their status to DynamoDB """
def update_world_session_info():

    done_scanning = False
    gamelift_sessions_scanned = []
    scan_args = { "AliasId": os.environ['FLEET_ALIAS'], "Limit": 100 }

    iterations = 0
    while not done_scanning:
        try:
            response = gamelift.describe_game_sessions(**scan_args)
        except botocore.exceptions.ClientError as error:
            raise Exception('Error scanning Game Sessions: {}'.format(error))

        gamelift_sessions_scanned.extend(response.get('GameSessions', []))
        next_token = response.get('NextToken')
        scan_args['NextToken'] = next_token

        if next_token is None:
            done_scanning = True
        
        iterations += 1
        if iterations >= 5 and done_scanning == False:
            #Pause for a second every 5 iterations to avoid throttling
            time.sleep(1)
            iterations = 0

    print("Iterated through game sessions")
    print(gamelift_sessions_scanned)

    for game_session in gamelift_sessions_scanned:

        # If there is a terminating or terminated world AND there are no provisioning or active new replacement world yet deployed, remove the item completely from DynamoDB
        if game_session["Status"] == "TERMINATING" or game_session["Status"] == "TERMINATED":

            # Check to make sure there are no provisioning or active versions of this world before removing (this will later trigger a new deploy to replace the terminated world)
            activecount = 0
            for gs in gamelift_sessions_scanned:
                if gs["Location"] == game_session["Location"] and gs["Name"] == game_session["Name"] and (gs["Status"] == "PROVISIONING" or gs["Status"] == "ACTIVE"):
                    activecount += 1

            if activecount == 0:
                print("We only have a terminated version of the world " + game_session["Location"] + ":" + game_session["Name"] + ". So we'll remove that completely from the table to allow redeployment.")
                try:
                    table = dynamodb.Table(os.environ['WORLD_SESSIONS_TABLE'])
                    response = table.delete_item(
                        Key={
                            'Location': game_session["Location"],
                            'WorldID': game_session["Name"]
                        }
                    )
                except botocore.exceptions.ClientError as error:
                    raise Exception('Error deelting world session from DynamoDB {}'.format(error))
            else:
                print("There's multiple worlds with the same configuration, so let's not terminate from sessions to avoid overriding the active world.")

            # Always continue here as we're not updating data, we just either removed the entry or ignored it
            continue

        # Otherwise update the latest data of the world to DynamoDB
        attributeUpdates = {
                'Status': { 'Value' : game_session["Status"], 'Action' : 'PUT'},
                'GameSessionId':  { 'Value' : game_session["GameSessionId"], 'Action' : 'PUT'},
                'LastUpdatedTime': { 'Value' : int(round(datetime.utcnow().timestamp())), 'Action' : 'PUT'},
                'CurrentPlayerSessionCount': { 'Value' : game_session["CurrentPlayerSessionCount"], 'Action' : 'PUT'},
            }
        try:
            table = dynamodb.Table(os.environ['WORLD_SESSIONS_TABLE'])
            response = table.update_item(
                Key={
                    'Location': game_session["Location"],
                    'WorldID': game_session["Name"]
                },
                AttributeUpdates=attributeUpdates
            )
        except botocore.exceptions.ClientError as error:
            raise Exception('Error updating world session to DynamoDB: {}'.format(error))

""" Checks the need for creating new dynamic worlds """
def handle_dynamic_world_scaling(world_config, world_sessions):

    available_player_slots = 0 # We calculate all free slots across the instances of the dynamic world

    print("Checking if we need to deploy more dynamic instances for world: ", world_config["WorldID"])

    # Iterate through all the world sessions to count how many free slots we have total for this dynamic world
    for world_session in world_sessions:

        # Only check worlds that have the correct name. NOTE: We're using startswith here, don't use one WorldID:s name as a substring for another one!
        if world_session["WorldID"].startswith(world_config["WorldID"]):
            # Add free slots from worlds that are active
            if world_session["Location"] == world_config["Location"] and world_session["Status"] == "ACTIVE":
                print("Found and active instance of the dynamic world, add to available player slots")
                available_player_slots += world_config["MaxPlayers"] - world_session["CurrentPlayerSessionCount"]
                print("Current total of free slots: ", available_player_slots)

            # Also add free slots from worlds that are still provisioning
            world_session_creation_time = datetime.fromtimestamp(int(world_session["CreationTime"]))
            time_since_creation_seconds = (datetime.utcnow() - world_session_creation_time).total_seconds()
            if time_since_creation_seconds < 300 and world_session["Status"] == "PROVISIONING":
                print("Found aa provisioning world instance of the dynamic world that hasn't timed out yet. Add free slots of this too.")
                available_player_slots += world_config["MaxPlayers"] - world_session["CurrentPlayerSessionCount"]
                print("Current total of free slots: ", available_player_slots)

    # If we have less than the amount of sessions in a single instance of the world, we'll spin up a new one
    # NOTE: You would modify this logic to your scaling needs
    if available_player_slots < world_config["MaxPlayers"]:
        print("We have less than one full world of free player slots, provision another instance of the dynamic world")
        # Select a dynamic name for this instance of the world by adding the date
        world_config["WorldID"] = world_config["WorldID"] + datetime.utcnow().strftime('_%Y%m%d_%H%M%S')
        print("Creating world with ID: ", world_config["WorldID"])
        deploy_world(world_config)

""" Goes through all the world configs and running sessions and provisions missing worlds """
def check_worlds_status_and_deploy():
    
    # Scan through all the worlds we want to have running globally
    worlds_configured = scan_worlds(os.environ['WORLD_CONFIGURATIONS_TABLE'])
    print("Worlds configured: ", worlds_configured)

    # Scan through all the world sessions
    world_sessions = scan_worlds(os.environ['WORLD_SESSIONS_TABLE'])
    print("World sessions: ", world_sessions)

    # Check for worlds that are not provisioned and not reporting healthly
    for world_config in worlds_configured:

        # Manage dynamic worlds separately, as we add more of them as needed
        if "DynamicWorld" in world_config.keys() and world_config["DynamicWorld"] == "YES":
            handle_dynamic_world_scaling(world_config, world_sessions)
            continue;

        world_deployed = False

        # Don't even check worlds that are defined to be terminated
        if "TerminateSession" in world_config.keys() and world_config["TerminateSession"] == "YES":
            #print("Ignore world " + world_config["WorldID"] + " as it's scheduled for termination")
            continue;

        # Check if the world session is provisioned within the past 5 minutes or reporting healthy
        for world_session in world_sessions:
            if world_session["WorldID"] == world_config["WorldID"] and world_session["Location"] == world_config["Location"]:
                world_deployed = is_world_deployed(world_session, world_config)
                break;

        # If the world is not deployed, provision it in the GameLift Fleet
        if world_deployed == False:
            print("World " + world_config["WorldID"] + " not deployed yet. Provision on GameLift Fleet")
            deploy_world(world_config)

""" The main entry point of the Lambda function """
def handler(event, context):

    update_world_session_info()
    check_worlds_status_and_deploy()

