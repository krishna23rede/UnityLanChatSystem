using System.Net;
using System.Net.Sockets;
using System.Text;

List<TcpClient> clients = new();

TcpListener listener = new TcpListener(IPAddress.Any, 5000);
listener.Start();

Console.WriteLine("Server Started");

while (true)
{
    TcpClient client = await listener.AcceptTcpClientAsync();
    clients.Add(client);

    // Get the IP address when they first connect
    IPEndPoint? remoteIpEndPoint = client.Client.RemoteEndPoint as IPEndPoint;
    string clientIP = remoteIpEndPoint?.Address.ToString() ?? "Unknown IP";

    Console.WriteLine($"Client Connected from: {clientIP}");

    // Pass the IP address into the handler so we can use it later
    _ = HandleClient(client, clientIP);
}

async Task HandleClient(TcpClient client, string clientIP)
{
    NetworkStream stream = client.GetStream();
    byte[] buffer = new byte[1024];

    try
    {
        while (true)
        {
            int bytesRead = await stream.ReadAsync(buffer);

            if (bytesRead == 0)
                break;

            string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // You can now log the message alongside who sent it on the server console
            Console.WriteLine($"[{clientIP}]: {message}");

            foreach (TcpClient c in clients)
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                await c.GetStream().WriteAsync(data);
            }
        }
    }
    catch
    {
        // Handle unexpected disconnects cleanly
    }

    clients.Remove(client);
    client.Close();

    // Log which specific IP disconnected
    Console.WriteLine($"Client Disconnected: {clientIP}");
}