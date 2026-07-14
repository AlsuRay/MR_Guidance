using UnityEngine;
using System.Net;
using System.Net.Sockets;
using System.Text;

public class UDPTest : MonoBehaviour
{
    private UdpClient client;
    
    void Start()
    {
        client = new UdpClient(5008);
        client.BeginReceive(OnReceive, null);
        Debug.Log("[UDPTest] Listening on port 5008...");
    }
    
    void OnReceive(System.IAsyncResult result)
    {
        IPEndPoint ep = new IPEndPoint(IPAddress.Any, 0);
        byte[] data = client.EndReceive(result, ref ep);
        string msg = Encoding.UTF8.GetString(data);
        Debug.Log($"[UDPTest] RECEIVED: {msg}");
        client.BeginReceive(OnReceive, null);
    }
}
