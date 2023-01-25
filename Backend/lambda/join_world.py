""" Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved. """
""" SPDX-License-Identifier: MIT-0 """

import json
import boto3
import botocore
from boto3.dynamodb.conditions import Key, Attr
import os
from datetime import datetime
import time
from decimal import *

dynamodb = boto3.resource('dynamodb')
gamelift = boto3.client('gamelift')

""" Increases the world player count by one in DynamoDB"""
def increase_player_count(location, world):
    try:
        table = dynamodb.Table(os.environ['WORLD_SESSIONS_TABLE'])
        response = table.update_item(
            Key={
                'Location': location,
                'WorldID': world
            },
            UpdateExpression='SET CurrentPlayerSessionCount = CurrentPlayerSessionCount + :value',
            ExpressionAttributeValues={
                ':value': 1
            }
        )
    except botocore.exceptions.ClientError as error:
        raise Exception('Error updating world session to DynamoDB: {}'.format(error))

""" The main entry point of the Lambda function """
def handler(event, context):

    print(event)

    location = event["queryStringParameters"]["location"]
    world_id = event["queryStringParameters"]["world_id"]
    player_id = event["requestContext"]["identity"]["cognitoIdentityId"]

    # Get the requested world
    table = dynamodb.Table(os.environ['WORLD_SESSIONS_TABLE'])
    response = table.get_item(
        Key={
            'Location': location,
            'WorldID': world_id
        }
    )
    
    item = response.get('Item')

    if item == None:
        return {
            "statusCode": 500,
            "body": { "Error" : "World doesn't exist"}
        }

    # Only try to reserve a slot if the world is initialized
    if item.get('CurrentPlayerSessionCount') != None:
        try:
             # If it has free player slots, try to request one
            if int(item['CurrentPlayerSessionCount']) < int(item['MaxPlayers']):
                print("There's free player sessions, try to claim one")
                response = gamelift.create_player_session(
                    GameSessionId=item['GameSessionId'],
                    PlayerId=player_id,
                    #PlayerData='string'
                )
                # Successful player placement, increase player sessions in DynamoDB by one (as this is synced only every 1 minute)
                increase_player_count(location, world_id)
                # Return the session info
                return {
                    "statusCode": 200,
                    "body": json.dumps(response["PlayerSession"], default=str)
                }
            else:
                print("No space left in the world.")
                return {
                    "statusCode": 500,
                    "body": { "Error" : "No space left in the world"}
                }
        except botocore.exceptions.ClientError as error:
            raise Exception('Error adding player to session: {}'.format(error))
    else:
        print("World is not properly initialized yet, no player session count")
        return {
            "statusCode": 500,
            "body": { "Error" : "World is not ready yet"}
        }

    # Shouldn't ever reach this return statement
    return {
        "statusCode": 500,
        "body": {}
    }