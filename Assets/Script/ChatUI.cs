using System;
using TMPro;
using UnityEngine;

public class ChatUI : MonoBehaviour
{
    [SerializeField] private TMP_InputField ChatInputField;
    [SerializeField] private TMP_InputField ChatInputUsername;

    [SerializeField] private Transform contentParent;
    [SerializeField] private GameObject messagePrefab;

    [SerializeField] private ChatNetwork chatNetwork;
    [SerializeField] private ChatHistory chatHistory;

    // ------------------------------------------------------------------
    // Lifecycle
    // ------------------------------------------------------------------

    private void Start()
    {
        // Populate UI entirely from the history that ChatHistory already
        // loaded in its Awake() — the single source of truth.
        foreach (ChatMessage msg in chatHistory.GetHistory())
            SpawnBubble(msg.rawText);

        chatNetwork.OnMessageReceived += OnNetworkMessageReceived;
    }

    // ------------------------------------------------------------------
    // Network (messages received from other clients)
    // ------------------------------------------------------------------

    private void OnNetworkMessageReceived(string rawText)
    {
        // Remote messages have no parsed username/content here,
        // so store them with an empty username and the full text as content.
        chatHistory.AddMessage(string.Empty, rawText, rawText);
        SpawnBubble(rawText);
    }

    // ------------------------------------------------------------------
    // Local input
    // ------------------------------------------------------------------

    public void DisplayInputText()
    {
        if (string.IsNullOrWhiteSpace(ChatInputField.text)) return;

        string timestamp = DateTime.Now.ToString("[yyyy-MM-dd HH:mm]");
        string username  = ChatInputUsername.text;
        string content   = ChatInputField.text;

        string rawText =
            $"<color=#55FF55>{timestamp}</color> " +
            $"<color=#FF5555>{username}:</color> \n " +
            $"<color=#FFFFFF>{content}</color>";

        ChatInputField.text = string.Empty;

        // 1. Persist to in-memory history
        chatHistory.AddMessage(username, content, rawText);

        // 2. Show in UI
        SpawnBubble(rawText);

        // 3. Broadcast to server
        chatNetwork.SendMessage(rawText);
    }

    // ------------------------------------------------------------------
    // UI helpers
    // ------------------------------------------------------------------

    private void SpawnBubble(string rawText)
    {
        GameObject obj   = Instantiate(messagePrefab, contentParent);
        TMP_Text   label = obj.GetComponentInChildren<TMP_Text>();
        label.text       = rawText;
    }

    // Wire to a "Clear History" button if needed
    public void ClearChatHistory()
    {
        chatHistory.ClearHistory();

        foreach (Transform child in contentParent)
            Destroy(child.gameObject);
    }

    public void QuitApplication() => Application.Quit();
}