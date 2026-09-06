using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;

[Serializable]
public class RemoteControlMessage
{
    public float x;
    public float z;
    public float yaw;
    public bool grab;
    public bool release;
}

public class RemoteObjectServer : MonoBehaviour
{
    [Header("Network")]
    [SerializeField] private int port = 7777;
    [SerializeField] private TMP_Text statusText;

    [Header("Controlled object")]
    [SerializeField] private Transform controlledObject;
    [SerializeField] private float moveSpeed = 4f;
    [SerializeField] private float rotationSpeed = 120f;
    [SerializeField] private Transform holdPoint;
    [SerializeField] private float grabRadius = 1.5f;

    private TcpListener listener;
    private TcpClient connectedClient;
    private Thread acceptThread;
    private Thread readThread;
    private volatile bool running;

    private readonly ConcurrentQueue<string> incomingLines =
        new ConcurrentQueue<string>();

    private RemoteControlMessage currentInput =
        new RemoteControlMessage();

    private Rigidbody grabbedBody;

    private void Start()
    {
        if (controlledObject == null)
            controlledObject = transform;

        if (holdPoint == null)
            holdPoint = controlledObject;

        WriteStatus($"IP: {GetBestIPv4()}   PORT: {port}");
    }

    public void StartServer()
    {
        if (running)
            return;

        listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        running = true;

        acceptThread = new Thread(AcceptLoop);
        acceptThread.IsBackground = true;
        acceptThread.Start();

        WriteStatus($"Servidor escuchando en {GetBestIPv4()}:{port}");
    }

    private void AcceptLoop()
    {
        while (running)
        {
            try
            {
                TcpClient client = listener.AcceptTcpClient();
                ReplaceClient(client);
            }
            catch (SocketException)
            {
                break;
            }
        }
    }

    private void ReplaceClient(TcpClient client)
    {
        connectedClient?.Close();
        connectedClient = client;

        readThread = new Thread(() => ReadLoop(client));
        readThread.IsBackground = true;
        readThread.Start();
    }

    private void ReadLoop(TcpClient client)
    {
        NetworkStream stream = client.GetStream();
        byte[] buffer = new byte[1024];
        StringBuilder pendingText = new StringBuilder();

        while (running && client.Connected)
        {
            int count = stream.Read(buffer, 0, buffer.Length);

            if (count == 0)
                break;

            string chunk = Encoding.UTF8.GetString(buffer, 0, count);
            pendingText.Append(chunk);

            string text = pendingText.ToString();
            string[] lines = text.Split('\n');

            for (int i = 0; i < lines.Length - 1; i++)
                incomingLines.Enqueue(lines[i]);

            pendingText.Clear();
            pendingText.Append(lines[lines.Length - 1]);
        }
    }

    private void Update()
    {
        while (incomingLines.TryDequeue(out string line))
        {
            RemoteControlMessage message =
                JsonUtility.FromJson<RemoteControlMessage>(line);

            currentInput.x = Mathf.Clamp(message.x, -1f, 1f);
            currentInput.z = Mathf.Clamp(message.z, -1f, 1f);
            currentInput.yaw = Mathf.Clamp(message.yaw, -1f, 1f);

            if (message.grab)
                TryGrab();

            if (message.release)
                Release();
        }

        ApplyMovement();
    }

    private void ApplyMovement()
    {
        Vector3 movement =
            new Vector3(currentInput.x, 0f, currentInput.z);

        controlledObject.Translate(
            movement * moveSpeed * Time.deltaTime,
            Space.World
        );

        controlledObject.Rotate(
            Vector3.up,
            currentInput.yaw * rotationSpeed * Time.deltaTime,
            Space.World
        );
    }

    private void TryGrab()
    {
        if (grabbedBody != null)
            return;

        Collider[] hits =
            Physics.OverlapSphere(controlledObject.position, grabRadius);

        Collider nearest = hits
            .Where(hit => hit.GetComponent<GrabbableObject>() != null)
            .OrderBy(hit => Vector3.Distance(
                controlledObject.position,
                hit.transform.position))
            .FirstOrDefault();

        if (nearest == null)
            return;

        grabbedBody = nearest.attachedRigidbody;

        if (grabbedBody != null)
            grabbedBody.isKinematic = true;

        nearest.transform.SetParent(holdPoint);
        nearest.transform.localPosition = Vector3.forward;
        nearest.transform.localRotation = Quaternion.identity;
    }

    private void Release()
    {
        if (grabbedBody == null)
            return;

        Transform released = grabbedBody.transform;
        released.SetParent(null);

        grabbedBody.isKinematic = false;
        grabbedBody = null;
    }

    private void OnApplicationQuit()
    {
        StopServer();
    }

    private void StopServer()
    {
        running = false;
        connectedClient?.Close();
        listener?.Stop();
        acceptThread?.Join(100);
        readThread?.Join(100);
    }

    private void WriteStatus(string message)
    {
        Debug.Log(message);

        if (statusText != null)
            statusText.text = message;
    }

    private static string GetBestIPv4()
    {
        foreach (NetworkInterface ni
                 in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (UnicastIPAddressInformation ip
                     in ni.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily ==
                        AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(ip.Address))
                {
                    return ip.Address.ToString();
                }
            }
        }

        return "0.0.0.0";
    }
}