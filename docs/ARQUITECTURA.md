# Arquitectura y mantenimiento

La solución separa el cálculo y el protocolo, que se pueden probar en Linux o Windows, de la interfaz y la supervisión de recursos de Windows. El destino inicial es CPU x64 y Stratum V1 de Bitcoin.

```text
Mining.slnx
├── src/Mining.Core       Modelos, SHA-256 doble, motor CPU, Stratum y sesión
├── src/Mining.Desktop    Interfaz WinForms, preferencias y estado del equipo
├── tests/Mining.Tests    Ejecutable de pruebas con salida no cero al fallar
├── scripts              Publicación del ejecutable independiente
└── .github/workflows    Compilación y pruebas en Windows
```

## Componentes

| Componente | Responsabilidad |
| --- | --- |
| `MinerSettings`, `MiningJob`, `MiningShare` | Configuración validada y datos intercambiados por el cliente y el motor. |
| `Hashing` | Construcción de encabezados Bitcoin, SHA-256 doble y comparación contra el objetivo de dificultad. |
| `CpuMiner` | Hilos de cálculo, reparto de trabajo, descansos de CPU, pausa y cancelación. Entrega participaciones mediante un canal acotado. |
| `StratumClient` | Conexión TCP, mensajes JSON por línea, suscripción, autorización, trabajos, dificultad y respuestas a participaciones. |
| `MiningSession` | Coordina cliente y motor; expone estado, estadísticas y registro a la interfaz. |
| `MainForm` | Recoge la intención del usuario, presenta estadísticas y mantiene controles de inicio/parada visibles. |
| `ResourceGuard` | Lee CPU y alimentación de Windows; determina la pausa automática por carga o batería. |
| `SettingsStore` | Lee y guarda preferencias locales mediante reemplazo del archivo para evitar escrituras parciales. |

El programa comienza sin sesión activa. La interfaz inicia una sesión únicamente por una acción del usuario. El cliente autoriza el trabajador antes de entregar trabajo de minería y coordina cambios de trabajo y conexión para no tratar participaciones antiguas como trabajo vigente. Los identificadores de solicitud permiten asociar cada respuesta del servidor con la operación correspondiente.

## Cálculo y consumo

Bitcoin utiliza SHA-256 doble sobre un encabezado de 80 bytes. El encabezado combina la versión, el hash del bloque anterior, la raíz Merkle, el tiempo, los bits de dificultad de red y el nonce. El cliente construye la transacción coinbase a partir de las partes del trabajo y los extranonces; combina su hash con las ramas Merkle recibidas. La dificultad de participaciones del pool determina el objetivo de envío y puede diferir de la dificultad de red.

Los órdenes de bytes del protocolo, del encabezado serializado y de la representación de hashes requieren conversiones explícitas. Cualquier cambio de optimización debe conservar los vectores conocidos y las pruebas de construcción del encabezado, además de medirse con el mismo número de hilos y presupuesto de CPU.

El motor alterna cálculo y descansos para aproximar un porcentaje de capacidad total de CPU. La configuración de hilos también limita la concurrencia. La aplicación y los hilos usan prioridad reducida; eso favorece la respuesta de otras tareas, sin imponer un límite estricto de recursos del sistema operativo.

`ResourceGuard` usa los contadores de Windows, no estimaciones de temperatura. La carga global alta activa una pausa con histéresis: tres muestras a partir de 85 % para pausar y cinco muestras hasta 65 % para permitir la recuperación. La pausa por recursos y la orden de pausa manual se combinan; desaparecer una causa automática no debe cancelar una pausa del usuario.

## Límites de confianza

Los mensajes del pool son entradas externas. El cliente limita las líneas, comprueba estructuras y datos y mantiene acotado el trabajo pendiente. La sesión no debe convertir un cierre de conexión, una autorización fallida o una respuesta desconocida en una participación aceptada.

El transporte implementado es TCP sin TLS. El puerto se configura por separado y no determina el cifrado. La contraseña de trabajador predeterminada es `x`; no se solicitan claves de cartera ni credenciales de la web de ViaBTC. Las preferencias del usuario se almacenan en su perfil de Windows, fuera del repositorio.

## Pruebas y distribución

`Mining.Tests` es un ejecutable de comprobaciones sin un framework externo. Usa vectores conocidos y conexiones locales simuladas para comprobar cálculos y comportamiento del protocolo con resultados reproducibles. Su código de salida permite que la integración continua detecte fallos.

```powershell
dotnet build Mining.slnx -c Release
dotnet run --project tests/Mining.Tests -c Release
```

La publicación usa `win-x64`, modo independiente y un solo ejecutable. Conserva la comprobación SHA-256 junto con el binario distribuido. El archivo [global.json](../global.json) fija el SDK; al actualizarlo, repite compilación, pruebas, publicación y aceptación manual en Windows.

La [lista de comprobación en Windows](GUIA-WINDOWS.md#comprobación-en-windows) cubre comportamiento de la interfaz, alimentación, cierre y procesos. Un servidor local simulado verifica el cliente, pero no acredita autorización de una cuenta real ni aceptación de participaciones por ViaBTC. Esas evidencias se deben registrar por separado.

## Ampliaciones

- **Motor más rápido:** conserva los modelos de trabajos y participaciones y la cancelación. Compara resultados con vectores de referencia antes de medir mejoras de rendimiento.
- **TLS:** añade transporte con validación de certificados y configuración explícita; no lo infieras del número de puerto.
- **Otros dispositivos:** agrega un controlador específico para el dispositivo. No presupongas que el motor CPU puede manejar un ASIC o GPU directamente.
- **Temperatura:** añade una fuente de sensores compatible y un estado explícito de lectura no disponible. No presentes el uso de CPU como temperatura.
- **Más pools:** separa los valores predeterminados y valida las diferencias de protocolo, autenticación y dificultad de cada proveedor.
- **Historial:** guarda datos de sesión con rotación y consentimiento visible. Conserva la diferencia entre trabajo calculado y participaciones aceptadas.

Para mantener cambios revisables, conserva el código en commits pequeños, ejecuta la suite antes de integrar cambios y utiliza la acción de Windows para producir el ejecutable. Los artefactos descargables tienen una retención limitada; los commits y las instrucciones de compilación son la fuente reproducible del proyecto.
