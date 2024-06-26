﻿// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using System;
using UnityEngine;
using System.Threading.Tasks;
using System.Net.Http;
using System.Collections.Generic;
using AWSSignatureV4_S3_Sample.Signers;

// **** MATCMAKING API CLIENT ***

public class BackendApiClient
{
#if CLIENT
    Client client; //The game client Monobehaviour

    public BackendApiClient()
    {
        // Find a reference to the client
        this.client = GameObject.FindObjectOfType<Client>();
    }

    public WorldsData RequestListWorlds(string region)
    {
        try
        {
            var response = Task.Run(() => this.SendSignedGetRequest(this.client.apiEndpoint + "listworlds?location=" + region));
            response.Wait(10000);
            string jsonResponse = response.Result;
            WorldsData worlds = JsonUtility.FromJson<WorldsData>(jsonResponse);
            return worlds;
        }
        catch (Exception e)
        {
            Debug.Log(e.Message);
            return null;
        }
    }

    public GameSessionInfo RequestJoinWorld(string location, string worldId)
    {
        try
        {
            var response = Task.Run(() => this.SendSignedGetRequest(this.client.apiEndpoint + "joinworld?location=" + location + "&world_id="+worldId));
            response.Wait(10000);
            string jsonResponse = response.Result;
            GameSessionInfo sessionInfo = JsonUtility.FromJson<GameSessionInfo>(jsonResponse);
            return sessionInfo;
        }
        catch (Exception e)
        {
            Debug.Log(e.Message);
            return null;
        }
    }

    // Helper function to send and wait for response to a signed request to the API Gateway endpoint
    async Task<string> SendSignedGetRequest(string requestUrl)
    {
        // Sign the request with cognito credentials
        var request = this.generateSignedRequest(requestUrl);

        // Execute the signed request
        var client = new HttpClient();
        var resp = await client.SendAsync(request);

        // Get the response
        var responseStr = await resp.Content.ReadAsStringAsync();
        Debug.Log(responseStr);

        return responseStr;
    }

    // Generates a HTTPS requestfor API Gateway signed with the Cognito credentials from a url using the S3 signer tool example
    // NOTE: You need to add the floders "Signers" and "Util" to the project from the S3 signer tool example: https://docs.aws.amazon.com/AmazonS3/latest/API/samples/AmazonS3SigV4_Samples_CSharp.zip
    HttpRequestMessage generateSignedRequest(string url)
    {
        var endpointUri = url;

        var uri = new Uri(endpointUri);

        var headers = new Dictionary<string, string>
            {
                {AWS4SignerBase.X_Amz_Content_SHA256, AWS4SignerBase.EMPTY_BODY_SHA256},
            };

        var signer = new AWS4SignerForAuthorizationHeader
        {
            EndpointUri = uri,
            HttpMethod = "GET",
            Service = "execute-api",
            Region = this.client.regionString
        };

        //Extract the query parameters
        var queryParams = "";
        if (url.Split('?').Length > 1)
        {
            queryParams = url.Split('?')[1];
        }

        var authorization = signer.ComputeSignature(headers,
                                                    queryParams,
                                                    AWS4SignerBase.EMPTY_BODY_SHA256,
                                                    Client.cognitoCredentials.AccessKey,
                                                    Client.cognitoCredentials.SecretKey);

        headers.Add("Authorization", authorization);

        var request = new HttpRequestMessage
        {
            Method = HttpMethod.Get,
            RequestUri = new Uri(url),
        };

        // Add the generated headers to the request
        foreach (var header in headers)
        {
            try
            {
                if (header.Key != null && header.Value != null)
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            catch (Exception e)
            {
                Debug.Log("error: " + e.GetType().ToString());
            }
        }

        // Add the IAM authentication token
        request.Headers.Add("x-amz-security-token", Client.cognitoCredentials.Token);

        return request;
    }
    #endif
}

