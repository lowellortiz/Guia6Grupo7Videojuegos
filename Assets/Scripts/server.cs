using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TMPro;
using UnityEngine;

/// <summary>
/// TcpChatServerUI
/// ----------------
/// Servidor de chat TCP para Unity con interfaz basada en TextMeshPro.
/// - Acepta múltiples clientes mediante TcpListener.
/// - Cada cliente se atiende en un hilo de lectura independiente para no bloquear el hilo principal de Unity.
/// - Los mensajes recibidos se reenvían a todos los clientes (broadcast) y se registran en la UI del servidor.
/// - Usa una ConcurrentQueue<string> para traspasar mensajes desde hilos de red al hilo principal (Update),
///   evitando acceso directo a la UI desde hilos secundarios (lo cual no es seguro en Unity).
/// - Incluye utilidades para detectar la IPv4 "correcta" del dispositivo (prioriza Wi‑Fi en Android).
///
/// Notas clave de arquitectura:
/// - _listener (TcpListener) escucha en IPAddress.Any para aceptar conexiones en cualquier interfaz.
/// - _clients guarda los TcpClient activos; su acceso está protegido por _clientsLock.
/// - _uiQueue almacena líneas de texto que serán drenadas en Update() para actualizar la UI.
/// - El método Broadcast permite opcionalmente reflejar (o no) el mensaje en la UI del servidor para evitar duplicados.
/// </summary>
public class TcpChatServerUI : MonoBehaviour
{
    #region Referencias de UI
    [Header("UI (lado izquierdo — Servidor)")]
    [SerializeField] private TMP_Text ipLabel;             // Muestra la IP local que deben usar los clientes.
    [SerializeField] private TMP_Text portLabel;           // Muestra el puerto en el que escucha el servidor.
    [SerializeField] private TMP_InputField portField;     // Campo para configurar el puerto antes de iniciar (opcional).
    [SerializeField] private TMP_InputField messageInput;  // Campo para escribir mensajes desde el servidor.
    [SerializeField] private TMP_Text msgArea;             // Área de log/chat multilínea donde se acumulan los mensajes.
    #endregion

    #region Configuración de red
    [Header("Red")]
    [SerializeField] private int defaultPort = 7777;       // Puerto por defecto si el usuario no especifica uno.

    private TcpListener _listener;                         // Socket pasivo que acepta nuevas conexiones TCP.
    private Thread _acceptThread;                          // Hilo dedicado a aceptar clientes para no bloquear el main thread.
    private volatile bool _running = false;                // Flag de ejecución del servidor.

    // Lista de clientes conectados; se accede bajo cerrojo ya que puede mutar desde varios hilos.
    private readonly List<TcpClient> _clients = new List<TcpClient>();
    private readonly object _clientsLock = new object();

    // Cola thread‑safe para pasar mensajes al hilo principal y actualizar la UI en Update().
    private readonly ConcurrentQueue<string> _uiQueue = new ConcurrentQueue<string>();
    #endregion

    #region Ciclo de vida Unity
    private void Start()
    {
        // Al iniciar, mostramos la IP "más adecuada" (prioriza Wi‑Fi en Android) y el puerto tentativo.
        UpdateIpLabel();
        int p = GetPortFromFieldOrDefault();
        if (portLabel) portLabel.text = $"PORT: {p}";

        // Si el campo de puerto está vacío, lo rellenamos con el valor efectivo.
        if (portField && string.IsNullOrWhiteSpace(portField.text))
            portField.text = p.ToString();
    }

    private void Update()
    {
        // Drenamos la cola de mensajes acumulados por los hilos de red.
        // Esto garantiza que las modificaciones de UI se realicen en el hilo principal.
        while (_uiQueue.TryDequeue(out var line))
        {
            if (msgArea != null)
                msgArea.text += (msgArea.text.Length > 0 ? "\n" : "") + line;
        }
    }

    private void OnApplicationQuit() => StopServer();
    private void OnDestroy() => StopServer();
    #endregion

    #region Callbacks de botones (UI)
    /// <summary>
    /// Inicia el servidor: crea el TcpListener en el puerto indicado, arranca el hilo de aceptación
    /// y actualiza los labels de IP/PORT. También vuelca en la UI todas las IPs candidatas para depuración.
    /// </summary>
    public void OnStartServer()
    {
        if (_running) return; // Evita múltiples inicios.

        int port = GetPortFromFieldOrDefault();
        try
        {
            // Escuchar en todas las interfaces (Wi‑Fi, Ethernet, etc.).
            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();
            _running = true;

            // Refresca UI con datos reales.
            if (portLabel) portLabel.text = $"PORT: {port}";
            UpdateIpLabel();

            EnqueueUi($"[Servidor] Escuchando en {GetBestIPv4ForUI()}:{port}");
            // Muestra todas las IPs candidatas por si existieran varias activas.
            DumpAllIPsToUI();

            // Hilo que acepta clientes de forma bloqueante sin congelar Unity.
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
            _acceptThread.Start();
        }
        catch (Exception ex)
        {
            // Si falla el bind/start, informamos en la UI. Ej.: puerto en uso, permisos, etc.
            EnqueueUi($"[Servidor] Error al iniciar: {ex.Message}");
        }
    }

    /// <summary>
    /// Envía un mensaje escrito en el input del servidor a todos los clientes.
    /// alsoShowOnServerUI = true para reflejarlo también en el log local.
    /// </summary>
    public void OnSendFromServer()
    {
        var msg = messageInput ? messageInput.text.Trim() : string.Empty;
        if (string.IsNullOrEmpty(msg)) return;
        Broadcast($"Servidor: {msg}", true);
        if (messageInput) messageInput.text = "";
    }

    /// <summary>
    /// Detiene el servidor y cierra todas las conexiones activas.
    /// </summary>
    public void OnStopServer()
    {
        StopServer();
        EnqueueUi("[Servidor] Detenido.");
    }

    /// <summary>
    /// Botón auxiliar para depurar: lista todas las IPv4 candidatas en la UI.
    /// </summary>
    public void OnShowIPsButton()
    {
        DumpAllIPsToUI();
    }
    #endregion

    #region Lógica de red (aceptación y manejo de clientes)
    /// <summary>
    /// Bucle de aceptación de clientes. Bloquea en AcceptTcpClient() y, por cada nuevo cliente,
    /// crea un hilo de lectura (ClientReadLoop) para procesar sus mensajes sin bloquear a los demás.
    /// </summary>
    private void AcceptLoop()
    {
        try
        {
            while (_running)
            {
                var client = _listener.AcceptTcpClient(); // Bloqueante hasta que entra un cliente.
                EnqueueUi($"[Servidor] Conectado: {client.Client.RemoteEndPoint}");

                lock (_clientsLock) _clients.Add(client); // Guarda el cliente para broadcasts futuros.

                // Hilo dedicado a leer del cliente.
                var t = new Thread(() => ClientReadLoop(client)) { IsBackground = true };
                t.Start();
            }
        }
        catch (SocketException)
        {
            // Ocurre normalmente al cerrar el listener durante StopServer(); no es un error crítico.
        }
        catch (Exception ex)
        {
            EnqueueUi($"[Servidor] Error AcceptLoop: {ex.Message}");
        }
    }

    /// <summary>
    /// Lee de un cliente específico en un hilo dedicado. Por cada línea recibida, la formatea con el
    /// endpoint de origen, la muestra una vez en la UI del servidor y la reenvía a todos los clientes.
    /// </summary>
    private void ClientReadLoop(TcpClient client)
    {
        string ep = client.Client.RemoteEndPoint?.ToString() ?? "cliente";
        try
        {
            using var stream = client.GetStream();
            byte[] buffer = new byte[4096];

            while (_running && client.Connected)
            {
                int read = stream.Read(buffer, 0, buffer.Length); // Bloqueante hasta que llega algo o se corta.
                if (read == 0) break; // Cero bytes = desconexión ordenada.

                string text = Encoding.UTF8.GetString(buffer, 0, read).Replace("\r", "");

                // El cliente puede enviar varias líneas en un paquete; las separamos por '\n'.
                foreach (var line in text.Split('\n'))
                {
                    if (!string.IsNullOrEmpty(line))
                    {
                        string formatted = $"{ep}: {line}";
                        EnqueueUi(formatted);                 // Mostrar UNA vez en la UI del servidor.
                        Broadcast(formatted, false);          // Reenviar a clientes SIN volver a escribir en UI.
                    }
                }
            }
        }
        catch
        {
            // Excepciones esperables: desconexión abrupta, stream roto, etc. Se maneja en finally.
        }
        finally
        {
            EnqueueUi($"[Servidor] Desconectado: {ep}");
            lock (_clientsLock) _clients.Remove(client);     // Retira el cliente de la lista activa.
            try { client.Close(); } catch { /* Ignorar fallos al cerrar */ }
        }
    }

    /// <summary>
    /// Envía un mensaje a todos los clientes conectados. Si alsoShowOnServerUI es true,
    /// también lo encola para mostrarse en la UI del servidor. Se eliminan clientes caídos.
    /// </summary>
    private void Broadcast(string message, bool alsoShowOnServerUI)
    {
        byte[] data = Encoding.UTF8.GetBytes(message + "\n");
        lock (_clientsLock)
        {
            for (int i = _clients.Count - 1; i >= 0; i--)
            {
                var c = _clients[i];
                try
                {
                    if (!c.Connected) { _clients.RemoveAt(i); continue; }
                    c.GetStream().Write(data, 0, data.Length);
                }
                catch
                {
                    // Si escribir falla, asumimos que el cliente está caído y lo retiramos.
                    _clients.RemoveAt(i);
                    try { c.Close(); } catch { }
                }
            }
        }

        if (alsoShowOnServerUI)
            EnqueueUi(message);
    }

    /// <summary>
    /// Detiene el servidor de forma segura: para el listener, cierra y limpia todos los clientes,
    /// y espera un poco al hilo de aceptación.
    /// </summary>
    private void StopServer()
    {
        if (!_running) return;
        _running = false;

        try { _listener?.Stop(); } catch { }

        lock (_clientsLock)
        {
            foreach (var c in _clients)
            {
                try { c.Close(); } catch { }
            }
            _clients.Clear();
        }

        try { _acceptThread?.Join(100); } catch { }
    }
    #endregion

    #region Utilidades de UI y configuración
    /// <summary> Encola una línea para ser añadida al área de mensajes en el próximo Update(). </summary>
    private void EnqueueUi(string line) => _uiQueue.Enqueue(line);

    /// <summary> Refresca el label de IP con la mejor IPv4 disponible (prioriza Wi‑Fi). </summary>
    private void UpdateIpLabel()
    {
        if (ipLabel) ipLabel.text = $"IP: {GetBestIPv4ForUI()}";
    }

    /// <summary>
    /// Devuelve el puerto elegido por el usuario en el InputField o, si no es válido, el puerto por defecto.
    /// </summary>
    private int GetPortFromFieldOrDefault()
    {
        if (portField && int.TryParse(portField.text, out int p) && p > 0 && p <= 65535)
            return p;
        return defaultPort;
    }

    /// <summary>
    /// Enlista en la UI todas las IPv4 candidatas detectadas (excluye loopback, túneles y 169.254.x.x).
    /// Útil para elegir manualmente si el dispositivo expone varias.
    /// </summary>
    private void DumpAllIPsToUI()
    {
        var ips = GetAllCandidateIPv4s();
        if (ips.Length == 0) { EnqueueUi("[Servidor] No se encontraron IPv4 válidas."); return; }

        EnqueueUi("[Servidor] IPs candidatas (elige la que coincide con tu Wi‑Fi):");
        foreach (var ip in ips) EnqueueUi(" - " + ip);
    }
    #endregion

    #region Detección de IPv4 (prioriza Wi‑Fi)
    /// <summary>
    /// Recorre todas las interfaces de red activas y retorna IPv4 válidas (no loopback, no túnel, no link‑local 169.254.x.x).
    /// </summary>
    private static string[] GetAllCandidateIPv4s()
    {
        var list = new List<string>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;               // Solo interfaces activas.
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;    // Excluye loopback.
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;      // Excluye túneles (VPN, etc.).

            var ipProps = ni.GetIPProperties();
            foreach (var ua in ipProps.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;  // Solo IPv4.
                var ip = ua.Address;
                if (IPAddress.IsLoopback(ip)) continue;                                // Excluye 127.0.0.1.

                // Excluye direcciones link‑local autoconfiguradas 169.254.x.x
                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;

                list.Add(ip.ToString());
            }
        }
        return list.Distinct().ToArray();
    }

    /// <summary>
    /// Intenta elegir la "mejor" IPv4 para mostrar: prioriza interfaces cuyo nombre/descripción
    /// sugiere Wi‑Fi (wlan, wifi, wlo, wl, wlp). Si no encuentra, devuelve la primera válida.
    /// </summary>
    private static string GetBestIPv4ForUI()
    {
        var ips = GetAllCandidateIPv4s();
        if (ips.Length == 0) return "0.0.0.0";

        // Pistas habituales para identificar interfaz Wi‑Fi.
        string[] wifiHints = { "wlan", "wifi", "wlo", "wl ", "wlp" };

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            var name = (ni.Name + " " + ni.Description).ToLowerInvariant();
            if (!wifiHints.Any(h => name.Contains(h))) continue; // No parece Wi‑Fi.

            var ipProps = ni.GetIPProperties();
            foreach (var ua in ipProps.UnicastAddresses)
            {
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    var ip = ua.Address.ToString();
                    if (ips.Contains(ip)) return ip; // Devuelve la IPv4 de esa interfaz.
                }
            }
        }

        // Si no identificamos claramente una interfaz Wi‑Fi, devolvemos la primera IPv4 válida.
        return ips[0];
    }
    #endregion
}