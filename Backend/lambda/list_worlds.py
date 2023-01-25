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

class DecimalEncoder(json.JSONEncoder):
    def default(self, obj):
        if isinstance(obj, Decimal):
            return str(obj)
        return json.JSONEncoder.default(self, obj)
    
""" Scans a DynamoDB table with pagination and returns the items in an array """
def query_worlds(table_name, location):

    table = dynamodb.Table(table_name)

    done_scanning = False
    worlds_queried = []
    query_args = { 'KeyConditionExpression' : Key('Location').eq(location),
                    'FilterExpression' : Attr('Status').eq("ACTIVE") }

    while not done_scanning:
        try:
            response = table.query(**query_args)
        except botocore.exceptions.ClientError as error:
            raise Exception('Error querying DynamoDB: {}'.format(error))

        worlds_queried.extend(response.get('Items', []))
        next_key = response.get('LastEvaluatedKey')
        query_args['ExclusiveStartKey'] = next_key

        if next_key is None:
            done_scanning = True

    return worlds_queried

""" The main entry point of the Lambda function """
def handler(event, context):

    print(event)

    location = event["queryStringParameters"]["location"]

    #Query the requested location
    worlds_queried = query_worlds(os.environ['WORLD_SESSIONS_TABLE'], location)
    
    worlds_response = { "Worlds" : worlds_queried }

    return {
        "statusCode": 200,
        "body": json.dumps(worlds_response, cls=DecimalEncoder)
    }
