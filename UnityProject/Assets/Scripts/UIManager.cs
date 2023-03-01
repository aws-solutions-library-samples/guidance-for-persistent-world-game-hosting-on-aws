// Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
// SPDX-License-Identifier: MIT-0

using UnityEngine.UI;
using UnityEngine;
using System;

public class UIManager : MonoBehaviour
{
    public UnityEngine.UI.Text statusTextBox;
    public UnityEngine.UI.Text statusSubTextBox;
    public Button listWorldsButton;
    public Dropdown regionDropdown;
    public GameObject worldInfoPrefab;
    public Transform worldInfoContentContainer;
    public Button restartButton;

    public void SetInfoTextBox(string text)
    {
        this.statusTextBox.text = text;
    }

    public void SetInfoSubTextBox(string text)
    {
        this.statusSubTextBox.text = text;
    }

    internal void HideMainMenuUI()
    {
        this.listWorldsButton.gameObject.SetActive(false);
        this.regionDropdown.gameObject.SetActive(false);
        this.worldInfoContentContainer.parent.parent.gameObject.SetActive(false);
    }

    internal GameObject AddWorldItemToList(WorldsData.WorldData world)
    {
        var item = Instantiate(this.worldInfoPrefab);

        if(world.DynamicWorld == "YES")
        {
            // Only show the name of the dynamic world without the extension by splitting with _
            item.GetComponentInChildren<Text>().text = "DYNAMIC: " + world.WorldID.Split('_')[0] + " Players: " + world.CurrentPlayerSessionCount + " / " + world.MaxPlayers + " Map: " + world.WorldMap;
            item.GetComponentInChildren<Text>().color = Color.yellow;
            // parent the item to the content container
            item.transform.SetParent(this.worldInfoContentContainer);
            // reset the scale
            transform.transform.localScale = Vector2.one;
        }
        else
        {
            item.GetComponentInChildren<Text>().text = world.WorldID + " Players: " + world.CurrentPlayerSessionCount + " / " + world.MaxPlayers + " Map: " + world.WorldMap;
            // parent the item to the content container
            item.transform.SetParent(this.worldInfoContentContainer);
            // reset the scale
            transform.transform.localScale = Vector2.one;
        }

        return item;
    }
}
