// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

import { Stack, StackProps } from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as iam from "aws-cdk-lib/aws-iam";
import { NagSuppressions } from 'cdk-nag';

export class PersistentWorldFleetRoleStack extends Stack {

  public readonly fleetRole : iam.Role;

  constructor(scope: Construct, id: string, props?: StackProps) {
    super(scope, id, props);

    this.fleetRole = new iam.Role(this, 'GameLiftFleetRole', {
      assumedBy: new iam.CompositePrincipal(
        new iam.ServicePrincipal("gamelift.amazonaws.com"),
        new iam.ServicePrincipal("ec2.amazonaws.com"))
    });
    //this.fleetRole.addManagedPolicy(iam.ManagedPolicy.fromAwsManagedPolicyName('CloudWatchAgentServerPolicy')); //not allowed for cdk-nag
    // Set up the CloudWatch Agent policy (same as the managed policy CloudWatchAgentServerPolicy)
    this.fleetRole.addToPolicy(
      new iam.PolicyStatement({
        actions: ['cloudwatch:PutMetricData','ec2:DescribeVolumes','ec2:DescribeTags','logs:PutLogEvents','logs:DescribeLogStreams','logs:DescribeLogGroups',
                  'logs:CreateLogStream','logs:CreateLogGroup'],
        resources: ['*'],
      }));
    this.fleetRole.addToPolicy(
        new iam.PolicyStatement({
          actions: ['ssm:GetParameter'],
          resources: ['arn:aws:ssm:*:*:parameter/AmazonCloudWatch-'],
        }));
    // cdk-nag suppression for the standard CloudWatch Agent policy of the fleet role
    NagSuppressions.addResourceSuppressions(
      this.fleetRole,
      [
        {
          id: 'AwsSolutions-IAM5',
          reason: "We are using a policy similar to the standard managed Lambda policy to access logs",
        },
      ],
      true
    );
  }
}
