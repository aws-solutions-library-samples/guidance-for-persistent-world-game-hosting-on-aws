// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

import { Duration, RemovalPolicy, Stack, StackProps } from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as lambda from "aws-cdk-lib/aws-lambda";
import * as events from "aws-cdk-lib/aws-events";
import * as targets from "aws-cdk-lib/aws-events-targets";
import * as dynamodb from "aws-cdk-lib/aws-dynamodb";
import * as iam from "aws-cdk-lib/aws-iam";
import * as apigateway from "aws-cdk-lib/aws-apigateway";
import * as identityPool from "@aws-cdk/aws-cognito-identitypool-alpha";
import { aws_gamelift as gamelift } from 'aws-cdk-lib';
import * as assets from "aws-cdk-lib/aws-s3-assets";
import * as path from 'path';
import * as cdk from 'aws-cdk-lib/core';
import * as logs from 'aws-cdk-lib/aws-logs'
import { CfnOutput } from 'aws-cdk-lib';
import { MethodLoggingLevel } from 'aws-cdk-lib/aws-apigateway';
import { NagSuppressions } from 'cdk-nag';

// extend the props of the stack by adding the vpc type from the SharedInfraStack
interface PersistentWorldGameLiftStackProps extends cdk.StackProps {
  gameliftFleetRole: iam.Role;
}

export class PersistentWorldGameliftStack extends Stack {
  constructor(scope: Construct, id: string, props: PersistentWorldGameLiftStackProps) {
    super(scope, id, props);

    const fleetLocation1 = "us-east-1";
    const fleetLocation2 = "us-west-2";
    const fleetLocation3 = "eu-west-1";

    /// AMAZON GAMELIFT FLEET ///

    // Import the Fleet role to allow adding permissions
    const importedGameLiftFleetRole = iam.Role.fromRoleArn(
      this,
      'GameLiftFleetRole',
      props.gameliftFleetRole.roleArn,
      {mutable: true},
    );

    // Set up the build
    const gameliftBuildRole = new iam.Role(this, 'GameLiftBuildRole', {
      assumedBy: new iam.ServicePrincipal('gamelift.amazonaws.com'),
    });
    const asset = new assets.Asset(this, 'BuildAsset', {
      path: path.join(__dirname, '../../LinuxServerBuild'),
    });
    gameliftBuildRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ['s3:GetObject', 's3:GetObjectVersion'],
        resources: [asset.bucket.bucketArn+'/'+asset.s3ObjectKey],
      }));
    //asset.bucket.grantRead(gameliftBuildRole); // Too permissive for cdk-nag

    const now = new Date();
    const gameliftBuild = new gamelift.CfnBuild(this, "Build", {
          name: 'GameLiftPersistentWorldBuild '+ now.toUTCString(),
          operatingSystem: 'AMAZON_LINUX_2',
          storageLocation: {
            bucket: asset.bucket.bucketName,
            key: asset.s3ObjectKey,
            roleArn: gameliftBuildRole.roleArn,
          },
          version: now.toUTCString()
     });
            
    gameliftBuild.node.addDependency(asset);
    gameliftBuild.node.addDependency(gameliftBuildRole);
    gameliftBuild.applyRemovalPolicy(RemovalPolicy.RETAIN); //Retain old builds
    var buildId = gameliftBuild.ref;

    // The multi-region fleet
    const persistentWorldFleet = new gamelift.CfnFleet(this, 'Example Persistent World Fleet', {
      buildId: buildId,
      name: 'ExamplePersistentWorldFleet',
      description: 'Example Persistent World Fleet',
      ec2InboundPermissions: [{
          fromPort: 1935,
          ipRange: '0.0.0.0/0',
          protocol: 'TCP',
          toPort: 1935,
        }, {
          fromPort: 7777,
          ipRange: '0.0.0.0/0',
          protocol: 'TCP',
          toPort: 7777,
      }],
      ec2InstanceType: 'c5.xlarge',
      fleetType: 'ON_DEMAND',
      instanceRoleArn: props.gameliftFleetRole.roleArn,
      locations: [{
        location: fleetLocation1,
          // the properties below are optional
          locationCapacity: {
            desiredEc2Instances: 1,
            maxSize: 1,
            minSize: 1
          }
        }, {
          location: fleetLocation2,
          // the properties below are optional
          locationCapacity: {
            desiredEc2Instances: 1,
            maxSize: 1,
            minSize: 1
          }
        }, {
            location: fleetLocation3,
            // the properties below are optional
            locationCapacity: {
              desiredEc2Instances: 1,
              maxSize: 1,
              minSize: 1
            }
      }],
      // NOTE: Set this to FullProtection for production Fleets.
      // Once you do that, GameLift will NOT be able to scale down and terminate your previous Fleet when doing a redeployment with CDK
      // You can instead configure CDK to retain the old fleets with a Removal Policy set to RETAIN. And then terminate them more controlled when empty from players
      newGameSessionProtectionPolicy: 'NoProtection',
      runtimeConfiguration: {
        serverProcesses: [{
          concurrentExecutions: 1,
          launchPath: '/local/game/GameLiftExampleServer.x86_64',
          // the properties below are optional
          parameters: '-logFile /local/game/logs/myserver1935.log -port 1935'
        },{
          concurrentExecutions: 1,
          launchPath: '/local/game/GameLiftExampleServer.x86_64',
          // the properties below are optional
          parameters: '-logFile /local/game/logs/myserver7777.log -port 7777'
        }]
      }
    });
    persistentWorldFleet.node.addDependency(gameliftBuild);

    // Alias for the fleet
    const fleetAlias = new gamelift.CfnAlias(this, 'PersistentWorldFleetAlias', {
      name: 'PersistentWorldFleetAlias',
      routingStrategy: {
        type: 'SIMPLE',
        fleetId: persistentWorldFleet.attrFleetId,
      }
    });

    // DynamoDB table for persisting world speicific player data
    const worldsPlayerDataTable = new dynamodb.Table(this, 'WorldsPlayerData', {
      partitionKey: { name: 'Location_WorldID', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'PlayerCognitoID', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      replicationRegions: [fleetLocation2, fleetLocation3]
    });
    // Allow GameLift instances to read and write world player data
    worldsPlayerDataTable.grantReadWriteData(importedGameLiftFleetRole);

    /// FUNCTIONS SHARED ///

    // The shared policy for basic Lambda access needs for logging. This is similar to the managed Lambda Execution Policy
    const lambdaBasicPolicy = new iam.PolicyStatement({
      actions: ['logs:CreateLogGroup','logs:CreateLogStream','logs:PutLogEvents'],
      resources: ['*'],
    });

    /// WORLD MANAGER ///

    // DynamoDB table for world configuration
    const worldsTable = new dynamodb.Table(this, 'WorldsConfiguration', {
      partitionKey: { name: 'Location', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'WorldID', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      replicationRegions: [fleetLocation2, fleetLocation3]
    });

    // Allow GameLift instances to read world config to check for termination
    worldsTable.grantReadData(importedGameLiftFleetRole);

    // DynamoDB table for running world sessions
    const worldSessions = new dynamodb.Table(this, 'WorldSessions', {
      partitionKey: { name: 'Location', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'WorldID', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST
    });

    // World manager function, invoked every minute to check world status and deploy new worlds as needed
    const worldManagerExecutionRole = new iam.Role(this, 'WorldManagerFunctionRole', {
      assumedBy: new iam.ServicePrincipal('lambda.amazonaws.com'),
    });
    const worldManagerFunction = new lambda.Function(this, "WorldManager", {
      role: worldManagerExecutionRole,
      runtime: lambda.Runtime.PYTHON_3_9,
      code: lambda.Code.fromAsset("lambda"),
      handler: "world_manager.handler",
      tracing: lambda.Tracing.ACTIVE,
      timeout: Duration.seconds(300), //The function is run every minute, but we account for corner cases where a lot of worlds are deployed at once
      memorySize : 512,
      environment: {
        WORLD_CONFIGURATIONS_TABLE: worldsTable.tableName,
        WORLD_SESSIONS_TABLE: worldSessions.tableName,
        FLEET_ALIAS: fleetAlias.attrAliasId,
        FLEET_ROLE_ARN: props.gameliftFleetRole.roleArn, // This is passed to the gameserver for DynamoDB access
        WORLD_PLAYER_DATA_TABLE: worldsPlayerDataTable.tableName // This is passed to the game server for world specific player data access
      },
      reservedConcurrentExecutions : 1 //Only allow one copy of the function to run at any given time
    });

    // Add basic execution
    worldManagerFunction.addToRolePolicy(lambdaBasicPolicy);
    // Add required access to DynamoDB tables
    worldsTable.grantReadData(worldManagerFunction);
    worldSessions.grantReadWriteData(worldManagerFunction);
    // Add access to GameLift APIs
    worldManagerFunction.addToRolePolicy(new iam.PolicyStatement({
      actions: ['gamelift:CreateGameSession', 'gamelift:DescribeGameSessions'],
      resources: ['*'] //GameLift Describe Actions don't support resources
    }));
    
    // Schedule the world manager to run every minute
    const eventRule = new events.Rule(this, 'scheduleRule', {
      schedule: events.Schedule.rate(Duration.minutes(1))
    });
    eventRule.addTarget(new targets.LambdaFunction(worldManagerFunction))

    // cdk-nag suppression for the Lambda access to logs (standard lambda execution role)
    NagSuppressions.addResourceSuppressions(
      worldManagerExecutionRole,
      [
        {
          id: 'AwsSolutions-IAM5',
          reason: "We are using a policy similar to the standard managed Lambda policy to access logs. Also, GameLift Describe API doesn't support resources.",
        },
      ],
      true
    );

    /// BACKEND API ///

    // Function to list worlds in specific location
    const listWorldsExecutionRole = new iam.Role(this, 'ListWorldsFunctionRole', {
      assumedBy: new iam.ServicePrincipal('lambda.amazonaws.com'),
    });
    const listWorldsFunction = new lambda.Function(this, "ListWorlds", {
      role: listWorldsExecutionRole,
      runtime: lambda.Runtime.PYTHON_3_9,
      code: lambda.Code.fromAsset("lambda"),
      handler: "list_worlds.handler",
      tracing: lambda.Tracing.ACTIVE,
      environment: {
        WORLD_SESSIONS_TABLE: worldSessions.tableName
      }
    });
    // Add basic execution
    listWorldsFunction.addToRolePolicy(lambdaBasicPolicy);
    // Allow access to read the session data
    worldSessions.grantReadData(listWorldsFunction);
    // cdk-nag suppression for the Lambda access to logs (standard lambda execution role)
    NagSuppressions.addResourceSuppressions(
      listWorldsExecutionRole,
      [
        {
          id: 'AwsSolutions-IAM5',
          reason: "We are using a policy similar to the standard managed Lambda policy to access logs",
        },
      ],
      true
    );

    // Function to join a player to a game world
    const joinWorldExecutionRole = new iam.Role(this, 'JoinWorldFunctionRole', {
      assumedBy: new iam.ServicePrincipal('lambda.amazonaws.com'),
    });
    const joinWorldFunction = new lambda.Function(this, "JoinWorld", {
      role: joinWorldExecutionRole,
      runtime: lambda.Runtime.PYTHON_3_9,
      code: lambda.Code.fromAsset("lambda"),
      handler: "join_world.handler",
      tracing: lambda.Tracing.ACTIVE,
      environment: {
        WORLD_SESSIONS_TABLE: worldSessions.tableName
      }
    });
    // Add basic execution
    joinWorldFunction.addToRolePolicy(lambdaBasicPolicy);
    // Allow access to read and write the session data (we need to update the current player count immediately)
    worldSessions.grantReadWriteData(joinWorldFunction);
    // Add access to relevantGameLift APIs
    joinWorldFunction.addToRolePolicy(new iam.PolicyStatement({
      actions: ['gamelift:CreatePlayerSession'],
      resources: ['*'],
    }));
    // cdk-nag suppression for the Lambda access to logs (standard lambda execution role)
    NagSuppressions.addResourceSuppressions(
      joinWorldExecutionRole,
      [
        {
          id: 'AwsSolutions-IAM5',
          reason: "We are using a policy similar to the standard managed Lambda policy to access logs. Also, GameLift Describe API doesn't support resources.",
        },
      ],
      true
    );

    // The API Gateway with access logging on
    const logGroup = new logs.LogGroup(this, "ApiGatewayAccessLogs");
    const api = new apigateway.RestApi(this, "gameworlds-api", {
      restApiName: "Game Worlds API",
      description: "This API is used by the game client to list and access game worlds",
      deployOptions: {
        accessLogDestination: new apigateway.LogGroupLogDestination(logGroup),
        accessLogFormat: apigateway.AccessLogFormat.clf(),
        loggingLevel : MethodLoggingLevel.ERROR
      },
    });

    // API Integrations to Lambda functions
    const listWorldsIntegration = new apigateway.LambdaIntegration(listWorldsFunction, {
      requestTemplates: { "application/json": '{ "statusCode": "200" }' },
    });

    // Request validator for the API
    const requestValidator = new apigateway.RequestValidator(this, 'GameWorldsAPIRequestValidator', {
      restApi: api,
    
      // the properties below are optional
      requestValidatorName: 'GameWorldsAPIRequestValidator',
      validateRequestBody: false,
      validateRequestParameters: true,
    });

    const listWorldsResource = api.root.addResource('listworlds').addMethod("GET", listWorldsIntegration, {
        authorizationType: apigateway.AuthorizationType.IAM,
        // Mark the parameters as required and validate
        requestParameters: {
          'method.request.querystring.location': true
        },
        requestValidator: requestValidator
      }
    );

    const joinWordIntegration = new apigateway.LambdaIntegration(joinWorldFunction, {
      requestTemplates: { "application/json": '{ "statusCode": "200" }' },
    });

    const joinWorldsResource = api.root.addResource('joinworld').addMethod("GET", joinWordIntegration, {
        authorizationType: apigateway.AuthorizationType.IAM,

        // Mark the parameters as required and validate
        requestParameters: {
          'method.request.querystring.location': true,
          'method.request.querystring.world_id': true,
        },
        requestValidator: requestValidator
      }
    );

    // cdk-nag suppression for the API Gateway default logs access
    NagSuppressions.addResourceSuppressions(
      api,
      [
        {
          id: 'AwsSolutions-IAM4',
          reason: "We are using the default CW Logs access of API Gateway",
        },
      ],
      true
    );

    /// COGNITO IDENTITY POOL FOR BACKEND API ACCESS ///

    const idPool = new identityPool.IdentityPool(this, 'myIdentityPool', { allowUnauthenticatedIdentities : true});
    // Grant access to call the API
    idPool.unauthenticatedRole.addToPrincipalPolicy(new iam.PolicyStatement({
      effect: iam.Effect.ALLOW,
      actions: ['execute-api:Invoke'],
      resources: [ listWorldsResource.methodArn, joinWorldsResource.methodArn ]
    }));

    // Output for the Identity pool
     new CfnOutput(this, 'IdentityPoolID', {
      value: idPool.identityPoolId,
      description: 'ID of the Cognito Identity Pool',
      exportName: 'IdentityPoolID',
    });
  }
}
