using System;
using System.Net.Sockets;
using System.Text;
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

    [Header("Controls")]
    [SerializeField] private Slider moveXSlider;
    [SerializeField] private Slider moveZSlider;
    [SerializeField] private Slider yawSlider;

    private TcpClient client;
    private NetworkStream stream;
    private float nextSendTime;
    private bool grabRequested;
    private bool releaseRequested;

    public void Connect()
    {
        string ip = ipField.text.Trim();
        int port = int.Parse(portField.text);

        client = new TcpClient();
        client.Connect(ip, port);
        stream = client.GetStream();

        SetStatus($"Conectado a {ip}:{port}");
    }

    private void Update()
    {
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
        stream.Write(data, 0, data.Length);

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
        stream?.Close();
        client?.Close();
        stream = null;
        client = null;
        SetStatus("Desconectado");
    }

    private void OnApplicationQuit()
    {
        Disconnect();
    }

    private void SetStatus(string message)
    {
        Debug.Log(message);
        if (statusText != null)
            statusText.text = message;
    }
}