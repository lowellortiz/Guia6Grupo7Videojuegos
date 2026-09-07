using System;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SmartphoneSocketController : MonoBehaviour
{
    [Header("Connection")]
    [SerializeField] private TMP_InputField ipField;
    [SerializeField] private TMP_InputField portField;
    [SerializeField] private TMP_Text statusText;
    [SerializeField] private float sendInterval = 0.05f;
    [SerializeField] private int connectTimeoutMs = 3000;

    [Header("Controls")]
    [SerializeField] private Slider moveXSlider;
    [SerializeField] private Slider moveZSlider;
    [SerializeField] private Slider yawSlider;

    private TcpClient client;
    private NetworkStream stream;
    private float nextSendTime;
    private bool grabRequested;
    private bool releaseRequested;

    private Thread connectThread;
    private volatile bool connecting;
    private volatile string pendingStatus;
    private volatile TcpClient pendingClient;

    public void Connect()
    {
        if (connecting)
            return;

        if (stream != null)
            CloseSocket();

        string ip = ipField.text.Trim();

        if (string.IsNullOrEmpty(ip))
        {
            SetStatus("Escribe la IP del servidor.");
            return;
        }

        if (!int.TryParse(portField.text.Trim(), out int port) || port < 1 || port > 65535)
        {
            SetStatus("Puerto invalido. Usa 7777.");
            return;
        }

        connecting = true;
        SetStatus($"Conectando a {ip}:{port}...");

        connectThread = new Thread(() => ConnectLoop(ip, port));
        connectThread.IsBackground = true;
        connectThread.Start();
    }

    // Se conecta en un hilo secundario. TcpClient.Connect bloquea hasta 20 s o mas
    // cuando la IP no responde (firewall, red distinta, IP equivocada), y hacerlo en
    // el hilo principal congelaria la aplicacion entera en el telefono.
    private void ConnectLoop(string ip, int port)
    {
        TcpClient candidate = new TcpClient();

        try
        {
            IAsyncResult attempt = candidate.BeginConnect(ip, port, null, null);

            if (!attempt.AsyncWaitHandle.WaitOne(connectTimeoutMs))
            {
                candidate.Close();
                pendingStatus = $"Sin respuesta de {ip}:{port}. Revisa la IP, que ambos esten en la misma Wi-Fi y el firewall del servidor.";
                return;
            }

            candidate.EndConnect(attempt);

            // El socket queda listo, pero es Update() quien lo adopta: GetStream()
            // y el resto del flujo viven en el hilo principal.
            pendingClient = candidate;
            pendingStatus = $"Conectado a {ip}:{port}";
        }
        catch (SocketException e)
        {
            candidate.Close();
            pendingStatus = $"No se pudo conectar ({e.SocketErrorCode}). Inicia el servidor y revisa IP y puerto.";
        }
        catch (Exception e)
        {
            candidate.Close();
            pendingStatus = $"No se pudo conectar: {e.Message}";
        }
        finally
        {
            connecting = false;
        }
    }

    private void Update()
    {
        // El hilo de conexion solo deja aqui su resultado; tocar la UI de Unity
        // desde un hilo secundario no esta permitido.
        if (pendingClient != null)
        {
            client = pendingClient;
            stream = client.GetStream();
            pendingClient = null;
        }

        if (pendingStatus != null)
        {
            SetStatus(pendingStatus);
            pendingStatus = null;
        }

        if (stream == null || Time.time < nextSendTime)
            return;

        SendCurrentInput();
        nextSendTime = Time.time + sendInterval;
    }

    private void SendCurrentInput()
    {
        RemoteControlMessage message = new RemoteControlMessage();
        message.x = moveXSlider.value;
        message.z = moveZSlider.value;
        message.yaw = yawSlider.value;
        message.grab = grabRequested;
        message.release = releaseRequested;

        string json = JsonUtility.ToJson(message) + "\n";
        byte[] data = Encoding.UTF8.GetBytes(json);

        try
        {
            stream.Write(data, 0, data.Length);
        }
        catch (Exception e)
        {
            // Si el servidor se cierra, Write falla en cada frame: hay que soltar
            // el socket en vez de seguir intentando.
            CloseSocket();
            SetStatus($"Conexion perdida: {e.Message}");
            return;
        }

        grabRequested = false;
        releaseRequested = false;
    }

    public void RequestGrab()
    {
        grabRequested = true;
    }

    public void RequestRelease()
    {
        releaseRequested = true;
    }

    public void Disconnect()
    {
        CloseSocket();
        SetStatus("Desconectado");
    }

    private void CloseSocket()
    {
        stream?.Close();
        client?.Close();
        stream = null;
        client = null;
    }

    private void OnApplicationQuit()
    {
        CloseSocket();
        connectThread?.Join(100);
    }

    private void SetStatus(string message)
    {
        Debug.Log(message);
        if (statusText != null)
            statusText.text = message;
    }
}
