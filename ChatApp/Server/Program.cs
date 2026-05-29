using System.Net;
using System.Net.Sockets;
using System.Text;

List<TcpClient> clients = new();

TcpListener listener =
    new TcpListener(IPAddress.Any, 5000);

listener.Start();

Console.WriteLine("Server Started");

while (true)
{
    TcpClient client =
        await listener.AcceptTcpClientAsync();

    clients.Add(client);

    Console.WriteLine("Client Connected");

    _ = HandleClient(client);
}

async Task HandleClient(TcpClient client)
{
    NetworkStream stream =
        client.GetStream();

    byte[] buffer = new byte[1024];

    try
    {
        while (true)
        {
            int bytesRead =
                await stream.ReadAsync(buffer);

            if (bytesRead == 0)
                break;

            string message =
                Encoding.UTF8.GetString(
                    buffer,
                    0,
                    bytesRead
                );

            Console.WriteLine(message);

            foreach (TcpClient c in clients)
            {
                byte[] data =
                    Encoding.UTF8.GetBytes(message);

                await c.GetStream()
                    .WriteAsync(data);
            }
        }
    }
    catch
    {
    }

    clients.Remove(client);

    client.Close();

    Console.WriteLine("Client Disconnected");
}