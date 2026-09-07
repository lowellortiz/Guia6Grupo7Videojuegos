# Guía 6 · Grupo 7 — Explicación técnica

Control remoto de un objeto 3D con sockets TCP puros (`System.Net.Sockets`).
Escenas: `SocketWorld3D_Server` (mundo 3D) y `SocketController_Client3D` (control táctil).

## IP : puerto

La IP identifica **el dispositivo** dentro de la red local; el puerto identifica **el programa**

El servidor abre un `TcpListener(IPAddress.Any, 7777)`: escucha en todas sus interfaces de red.
El cliente hace `TcpClient.Connect(ip, 7777)` contra esa dirección. Ambos deben estar en la misma
Wi-Fi, y el firewall del servidor debe permitir la entrada por ese puerto.

## JSON

Es el formato acordado del mensaje. La clase `RemoteControlMessage` (`[Serializable]`) define cinco
campos: `x`, `z`, `yaw`, `grab` y `release`. `JsonUtility.ToJson()` la convierte en texto y
`JsonUtility.FromJson<T>()` la reconstruye al otro lado.

```
{"x":0.4,"z":1.0,"yaw":-0.2,"grab":false,"release":false}\n
```

Cliente y servidor comparten la misma clase, así que los campos nunca se desincronizan.

## Bytes

Un socket no transporta texto, solo octetos. `Encoding.UTF8.GetBytes(json)` convierte la cadena en
`byte[]` antes de enviarla, y `Encoding.UTF8.GetString(buffer, 0, count)` la reconstruye al recibirla.
UTF-8 es el acuerdo de codificación entre los dos extremos.

## Stream

`NetworkStream` es el flujo de bytes de la conexión: `stream.Write()` envía, `stream.Read()` recibe.

TCP garantiza orden y entrega, pero **no** respeta las fronteras de los mensajes: un `Read()` puede
devolver medio JSON, o dos y medio pegados. Por eso cada mensaje termina en `\n`, que actúa como
delimitador. El servidor acumula lo recibido en un `StringBuilder`, lo parte por `'\n'`, procesa las
líneas completas y guarda el fragmento sobrante para el siguiente ciclo.

## Hilos

`AcceptTcpClient()` y `stream.Read()` son llamadas **bloqueantes**: se quedan esperando hasta que
llega algo por la red. Si corrieran en el hilo principal, Unity se congelaría.

Por eso el servidor usa dos hilos secundarios: `AcceptLoop()` espera conexiones y `ReadLoop()` lee
bytes. En el cliente, `Connect()` también corre en un hilo aparte con timeout, porque conectar a una
IP que no responde bloquea 20 segundos o más.

Esos hilos **no pueden tocar la escena**: la API de Unity (`Transform`, `Rigidbody`, `Physics`) solo
es accesible desde el hilo principal. Las líneas leídas se depositan en un `ConcurrentQueue<string>`,
que es la frontera segura entre ambos mundos.

## Update

`Update()` corre en el hilo principal y es el único que modifica la escena. Cada frame:

1. Vacía la cola con `TryDequeue` y reconstruye cada mensaje con `JsonUtility.FromJson`.
2. Limita los ejes con `Mathf.Clamp(-1, 1)` para descartar valores fuera de rango.
3. Aplica movimiento con `Translate` y `Rotate`, multiplicando por `Time.deltaTime` para que la
   velocidad no dependa de los fps.
4. Si llega `grab`, busca el objeto cercano con `Physics.OverlapSphere`, lo emparenta al `HoldPoint`
   y le pone `isKinematic = true`. Si llega `release`, deshace ambas cosas.

## Flujo completo

```
Slider táctil (cliente, hilo principal)
  → JsonUtility.ToJson + "\n"
  → Encoding.UTF8.GetBytes
  → stream.Write   ══ TCP  IP:puerto ══>   stream.Read   (servidor, HILO SECUNDARIO)
  → StringBuilder + Split('\n')
  → ConcurrentQueue<string>        ← frontera entre hilos
  → Update()                        (servidor, HILO PRINCIPAL)
  → JsonUtility.FromJson + Mathf.Clamp
  → Transform.Translate / Rotate  ·  Physics.OverlapSphere
```
