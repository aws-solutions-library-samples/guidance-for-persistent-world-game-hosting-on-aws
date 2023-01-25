#!/usr/bin/env node
import 'source-map-support/register';
import * as cdk from 'aws-cdk-lib';
import { PersistentWorldGameliftStack } from '../lib/persistent-world-gamelift-stack';
import { PersistentWorldFleetRoleStack } from '../lib/persistent-world-fleet-role-stack';
import { AwsSolutionsChecks, NagSuppressions } from 'cdk-nag'
import { Aspects } from 'aws-cdk-lib';

var deploymentRegion = 'us-east-1';

const app = new cdk.App();

const fleetRoleStack = new PersistentWorldFleetRoleStack(app, 'PersistentWorldFleetRoleStack', {
  env: { account: process.env.CDK_DEFAULT_ACCOUNT, region: deploymentRegion },
});

const backendStack = new PersistentWorldGameliftStack(app, 'PersistentWorldGameliftStack', {
  env: { account: process.env.CDK_DEFAULT_ACCOUNT, region: deploymentRegion },
  gameliftFleetRole: fleetRoleStack.fleetRole
});

/* CDK Nag, enable as needed
// Add the cdk-nag AwsSolutions Pack with extra verbose logging enabled.
Aspects.of(app).add(new AwsSolutionsChecks({}))

// Suppress Cognito User Pool requirements, as we're using Identity Pools and AWS_IAM for access control to API
NagSuppressions.addStackSuppressions(backendStack, [
  { id: 'AwsSolutions-COG4', reason: 'Using Identity pools and AWS_IAM access control' },
]);

// We need unauthenticated access for guest users (standard in mobile applications)
NagSuppressions.addStackSuppressions(backendStack, [
  { id: 'AwsSolutions-COG7', reason: 'Using guest users (unauthenticated) for the game, which is standard for mobile games' },
]);
*/