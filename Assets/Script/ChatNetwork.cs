using System;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

public class ChatNetwork : MonoBehaviour
{
    private TcpClient client;
    private NetworkStream stream;

    private byte[] buffer = new byte[4096];

    public Action<string> OnMessageReceived;

    async void Start()
    {
        try
        {
            client = new TcpClient();

            await client.ConnectAsync("127.0.0.1", 5000);

            stream = client.GetStream();

            Debug.Log("Connected to server");

            ReceiveLoop();
        }
        catch (Exception e)
        {
            Debug.LogError("Connection failed: " + e.Message);
        }
    }

    public async void SendMessage(string message)
    {
        if (client == null || !client.Connected)
            return;

        byte[] data = Encoding.UTF8.GetBytes(message);

        await stream.WriteAsync(data, 0, data.Length);
    }

    async void ReceiveLoop()
    {
        while (true)
        {
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);

            if (bytesRead == 0)
                break;

            string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            OnMessageReceived?.Invoke(message);
        }
    }
}