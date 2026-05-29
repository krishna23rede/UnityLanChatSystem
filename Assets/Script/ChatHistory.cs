using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

[Serializable]
public class ChatMessage
{
    public string timestamp;
    public string username;
    public string content;
    public string rawText;
}

[Serializable]
public class ChatHistoryData
{
    public List<ChatMessage> messages = new List<ChatMessage>();
}

public class ChatHistory : MonoBehaviour
{
    // Stored inside the Unity project: Assets/chat_history.json
    // Application.dataPath points to the Assets folder at runtime in Editor,
    // and to the <AppName>_Data folder in a build.
    private string FilePath => Path.Combine(Application.dataPath, "chat_history.json");

    private ChatHistoryData historyData = new ChatHistoryData();

    // ------------------------------------------------------------------
    // Lifecycle — single read on load, single write on quit
    // ------------------------------------------------------------------

    private void Awake()
    {
        ReadFromFile();  // one read: populate historyData from disk
    }

    private void OnApplicationQuit()
    {
        WriteToFile();   // one write: flush everything to disk on exit
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns all messages currently held in memory.
    /// ChatUI calls this once in Start() to populate the scroll view.
    /// </summary>
    public List<ChatMessage> GetHistory() => historyData.messages;

    /// <summary>
    /// Appends a message to the in-memory list only.
    /// Nothing is written to disk until the application quits.
    /// </summary>
    public void AddMessage(string username, string content, string rawText)
    {
        historyData.messages.Add(new ChatMessage
        {
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            username  = username,
            content   = content,
            rawText   = rawText
        });
    }

    /// <summary>
    /// Clears history from memory and immediately deletes the file.
    /// </summary>
    public void ClearHistory()
    {
        historyData = new ChatHistoryData();

        if (File.Exists(FilePath))
            File.Delete(FilePath);

        Debug.Log("[ChatHistory] History cleared.");
    }

    // ------------------------------------------------------------------
    // File I/O
    // ------------------------------------------------------------------

    private void ReadFromFile()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                Debug.Log($"[ChatHistory] No file found at {FilePath} — starting fresh.");
                historyData = new ChatHistoryData();
                return;
            }

            string json = File.ReadAllText(FilePath);
            historyData = JsonUtility.FromJson<ChatHistoryData>(json)
                          ?? new ChatHistoryData();

            Debug.Log($"[ChatHistory] Loaded {historyData.messages.Count} message(s) from {FilePath}");
        }
        catch (Exception e)
        {
            Debug.LogError("[ChatHistory] Read failed: " + e.Message);
            historyData = new ChatHistoryData();
        }
    }

    private void WriteToFile()
    {
        try
        {
            string json = JsonUtility.ToJson(historyData, prettyPrint: true);
            File.WriteAllText(FilePath, json);
            Debug.Log($"[ChatHistory] Saved {historyData.messages.Count} message(s) → {FilePath}");
        }
        catch (Exception e)
        {
            Debug.LogError("[ChatHistory] Write failed: " + e.Message);
        }
    }
}