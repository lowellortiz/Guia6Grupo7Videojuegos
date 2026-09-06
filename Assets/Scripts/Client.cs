using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;

/// <summary>
/// TcpChatClientUI
/// ----------------
/// Cliente de chat TCP para Unity con interfaz basada en TextMeshPro.
/// - Se conecta a un servidor TCP en una IP/puerto especificados.
/// - Permite enviar mensajes desde un InputField.
/// - Recibe mensajes desde el servidor en un hilo dedicado y los encola para la UI.
/// - Usa ConcurrentQueue<string> para pasar mensajes desde hilos secundarios al hilo principal (Update),
///   evitando modificar la UI desde hilos distintos al principal.
/// </summary>
public class TcpChatClientUI : MonoBehaviour
{
    #region Referencias de UI
    [Header("UI (lado derecho — Cliente)")]
    [SerializeField] private TMP_InputField ipField;        // Campo de entrada para escribir la IP del servidor.
    [SerializeField] private TMP_InputField portField;      // Campo de entrada para escribir el puerto del servidor.
    [SerializeField] private TMP_InputField messageInput;   // Campo de entrada para escribir mensajes a enviar.
    [SerializeField] private TMP_Text msgArea;              // Área de texto multilínea donde se muestran los mensajes.
    #endregion

    #region Configuración de red
    [Header("Red")]
    [SerializeField] private int defaultPort = 7777;        // Puerto por defecto si el usuario no especifica uno.

    private TcpClient _client;                             // Cliente TCP subyacente.
    private Thread _readThread;                            // Hilo para leer datos del servidor sin bloquear el main thread.
    private volatile bool _connected = false;              // Estado de conexión.

    private readonly ConcurrentQueue<string> _uiQueue = new ConcurrentQueue<string>(); // Cola de mensajes pendientes para la UI.
    #endregion

    #region Ciclo de vida Unity
    private void Start()
    {
        // Prellenar el puerto si el campo está vacío al iniciar.
        if (portField && string.IsNullOrWhiteSpace(portField.text))
            portField.text = defaultPort.ToString();
    }

    private void Update()
    {
        // En cada frame, drenamos la cola de mensajes y los agregamos al área de texto.
        while (_uiQueue.TryDequeue(out var line))
        {
            if (msgArea != null)
                msgArea.text += (msgArea.text.Length > 0 ? "\n" : "") + line;
        }
    }

    private void OnApplicationQuit() => Disconnect();
    private void OnDestroy() => Disconnect();
    #endregion

    #region Callbacks de botones
    /// <summary>
    /// Botón Start: intenta conectarse al servidor usando la IP y el puerto indicados.
    /// Si la conexión es exitosa, arranca un hilo de lectura.
    /// </summary>
    public void OnConnect()
    {
        if (_connected) { EnqueueUi("[Cliente] Ya conectado."); return; }

        string ip = ipField ? ipField.text.Trim() : "";
        if (string.IsNullOrEmpty(ip)) { EnqueueUi("[Cliente] IP inválida."); return; }

        int port = GetPortFromFieldOrDefault();

        try
        {
            _client = new TcpClient();
            _client.Connect(ip, port); // Intento de conexión bloqueante.
            _connected = true;

            EnqueueUi($"[Cliente] Conectado a {ip}:{port}");

            // Iniciamos un hilo de lectura para escuchar mensajes del servidor.
            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();
        }
        catch (Exception ex)
        {
            EnqueueUi($"[Cliente] Error de conexión: {ex.Message}");
            _connected = false;
            try { _client?.Close(); } catch { }
        }
    }

    /// <summary>
    /// Botón Send: envía el texto escrito en messageInput al servidor.
    /// El propio cliente también lo refleja en su UI con prefijo "Yo: ...".
    /// </summary>
    public void OnSendFromClient()
    {
        if (!_connected || _client == null || !_client.Connected)
        {
            EnqueueUi("[Cliente] No conectado.");
            return;
        }
        string msg = messageInput ? messageInput.text.Trim() : string.Empty;
        if (string.IsNullOrEmpty(msg)) return;

        try
        {
            var data = Encoding.UTF8.GetBytes(msg + "\n");
            _client.GetStream().Write(data, 0, data.Length);
            EnqueueUi($"Yo: {msg}"); // Se refleja localmente.
            if (messageInput) messageInput.text = "";
        }
        catch (Exception ex)
        {
            EnqueueUi($"[Cliente] Error al enviar: {ex.Message}");
        }
    }

    /// <summary>
    /// Botón Disconnect: cierra la conexión de forma controlada.
    /// </summary>
    public void OnDisconnect() => Disconnect();
    #endregion

    #region Lógica de red (lectura y desconexión)
    /// <summary>
    /// Hilo de lectura: se ejecuta en background mientras la conexión esté activa.
    /// Lee mensajes del servidor y los encola para la UI.
    /// </summary>
    private void ReadLoop()
    {
        try
        {
            var stream = _client.GetStream();
            byte[] buffer = new byte[4096];
            while (_connected && _client.Connected)
            {
                int read = stream.Read(buffer, 0, buffer.Length); // Bloqueante hasta recibir datos o desconexión.
                if (read == 0) break; // Si se leen 0 bytes, el servidor cerró la conexión.

                string text = Encoding.UTF8.GetString(buffer, 0, read).Replace("\r", "");
                foreach (var line in text.Split('\n'))
                    if (!string.IsNullOrEmpty(line))
                        EnqueueUi(line);
            }
        }
        catch { /* Errores típicos de desconexión se ignoran. */ }
        finally
        {
            EnqueueUi("[Cliente] Desconectado.");
            _connected = false;
            try { _client?.Close(); } catch { }
        }
    }

    /// <summary>
    /// Cierra la conexión y detiene el hilo de lectura.
    /// </summary>
    private void Disconnect()
    {
        if (!_connected) return;
        _connected = false;
        try { _client?.Close(); } catch { }
        try { _readThread?.Join(100); } catch { }
    }
    #endregion

    #region Helpers
    /// <summary>
    /// Intenta leer el puerto desde el campo de texto; si no es válido, devuelve el puerto por defecto.
    /// </summary>
    private int GetPortFromFieldOrDefault()
    {
        if (portField && int.TryParse(portField.text, out int p) && p > 0 && p <= 65535)
            return p;
        return defaultPort;
    }

    /// <summary>
    /// Encola una línea de texto para ser procesada en la UI en el siguiente Update().
    /// </summary>
    private void EnqueueUi(string line) => _uiQueue.Enqueue(line);
    #endregion
}