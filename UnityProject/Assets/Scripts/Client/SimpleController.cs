// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

// A simple class for getting player input and moving a character

using System;
using UnityEngine;

public class SimpleController : MonoBehaviour
{
    float speed = 5.0f;

    float currentMoveZ = 0.0f;
    float currentMoveX = 0.0f;

    public float GetCurrentMoveX() { return currentMoveX; }
    public float GetCurrentMoveZ() { return currentMoveZ; }

    public void SetMove(float x, float z) { this.currentMoveX = x; this.currentMoveZ = z; }

#if CLIENT
    // Manage player input on a fixed timestep
    void FixedUpdate()
    {

        currentMoveX = 0.0f;
        currentMoveZ = 0.0f;

        // Get the movement input 
        if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.W))
        {
            //move.z = 1;
            currentMoveZ = 1.0f;
        }
        else if (Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.S))
        {
            // move.z = -1;
            currentMoveZ = -1.0f;
        }
        if (Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.A))
        {
            // move.x = -1;
            currentMoveX = -1.0f;
        }
        else if (Input.GetKey(KeyCode.RightArrow) || Input.GetKey(KeyCode.D))
        {
            //move.x = 1;
            currentMoveX = 1.0f;
        }
    }
#endif

    public void Move()
    {
        Vector3 move = Vector3.zero;
        move.x = currentMoveX;
        move.z = currentMoveZ;

        // Move the character
        move.Normalize();
        this.transform.LookAt(this.transform.position + move);
        this.GetComponent<CharacterController>().SimpleMove(move * speed);
    }

    internal void ForceSetPosition(float x, float y, float z)
    {
        // To warp the charactercontroller, we need to disable and enable it
        this.GetComponent<CharacterController>().enabled = false;
        this.GetComponent<CharacterController>().transform.position = new Vector3(x,y,z);
        this.GetComponent<CharacterController>().enabled = true;
    }
}