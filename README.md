# Minería Bitcoin para Windows

Aplicación de escritorio en español para minar Bitcoin mediante la **CPU**, con conexión Stratum a ViaBTC, inicio manual y controles para reducir la carga del equipo. Destinada a Windows 11 de 64 bits.

**La minería de Bitcoin por CPU tiene un rendimiento muy inferior al de un ASIC y no debe considerarse una fuente viable de ingresos.** Incluso conectado correctamente, el equipo puede pasar mucho tiempo sin encontrar una participación válida para la dificultad de ViaBTC. El programa muestra el trabajo y las respuestas reales del pool; no estima ganancias.

## Uso rápido

1. Descarga el [paquete inicial para Windows x64](downloads/MineriaBitcoin-win-x64-v0.1.0.zip), o el artefacto `MineriaBitcoin-win-x64` de una ejecución correcta en **Actions** para obtener una compilación posterior. También puedes compilarlo como se explica abajo.
2. Extrae el archivo descargado y abre el ejecutable. No necesita permisos de administrador ni una instalación independiente de .NET.
3. Introduce tu trabajador con el formato `cuenta.trabajador`, por ejemplo `micuenta.pc01`. Usa el nombre de tu cuenta de minería de ViaBTC. No introduzcas la contraseña de acceso a su web.
4. Revisa el servidor, los hilos y el objetivo de CPU. Empieza con el valor predeterminado de 25 % y las protecciones de batería y carga activadas.
5. Pulsa **Iniciar**. Puedes pausar, reanudar o detener la sesión. Al abrir la aplicación siempre comienza detenida; cerrar la ventana finaliza la minería.

Consulta la [guía para Windows](docs/GUIA-WINDOWS.md) para interpretar las estadísticas y diagnosticar conexiones.

## Funciones y límites

- Minería SHA-256 doble de encabezados Bitcoin y envío de participaciones mediante Stratum V1.
- Suscripción, autorización del trabajador, cambios de dificultad y trabajos nuevos del pool.
- Reconexión con espera progresiva y contadores de participaciones aceptadas, rechazadas y pendientes.
- Inicio y parada manuales, pausa, número de hilos y objetivo de uso de CPU de 5 % a 75 %.
- Prioridad de ejecución reducida y pausas automáticas cuando el equipo usa batería o la CPU del sistema está ocupada.
- Estadísticas de velocidad de cálculo, trabajo realizado y duración de sesión, además de registro exportable.
- Configuración local reutilizable y motor separado de la interfaz para facilitar mejoras.

El porcentaje de CPU es un **objetivo aproximado del proceso**, aplicado alternando trabajo y descansos. No es un límite estricto del sistema operativo ni una garantía de temperatura. No se leen sensores térmicos. La aplicación no controla GPU ni ASIC, no crea una cartera y no administra pagos; estos se configuran en ViaBTC.

El servidor inicial es `btc.viabtc.io:3333`. Los puertos 443 y 25 son alternativas que deben confirmarse con la documentación vigente de ViaBTC y tu red. Se usa **TCP sin cifrado**: el puerto 443 por sí solo no activa TLS. El programa utiliza `x` como contraseña del trabajador, nunca la contraseña de tu cuenta web.

## Compilar y probar

Instala el SDK de .NET indicado en [global.json](global.json). Los proyectos no agregan paquetes de terceros. Las herramientas de .NET pueden descargar sus paquetes oficiales de referencia y ejecución durante la compilación.

Desde la raíz del repositorio:

```powershell
dotnet build Mining.slnx -c Release
dotnet run --project tests/Mining.Tests -c Release
./scripts/publish-windows.ps1
```

El último comando produce `artifacts/windows/MineriaBitcoin.exe`, un ejecutable independiente para `win-x64`, junto con documentación y `SHA256SUMS.txt`. Incluye el entorno de ejecución, por lo que su tamaño es considerable. La carpeta `artifacts/` y los resultados de compilación están excluidos de Git; el código fuente, las pruebas y las instrucciones de compilación se conservan en el repositorio.

Para comprobar la integridad de una copia descargada en PowerShell:

```powershell
Get-FileHash .\MineriaBitcoin.exe -Algorithm SHA256
```

Compara el resultado con `SHA256SUMS.txt` del mismo paquete. El ejecutable de desarrollo no tiene firma de editor; un checksum detecta cambios del archivo, pero no sustituye una firma de código.

La acción de GitHub compila en Windows, ejecuta la suite, comprueba la apertura en reposo y el cierre de la ventana y publica un artefacto descargable con retención de 90 días. El código fuente permite reconstruirlo después de ese plazo. Además, el paquete inicial de `downloads/` queda conservado en Git. Crear un commit o compilar desde Linux no demuestra por sí solo que esa acción haya terminado correctamente.

## Validación y desarrollo

Las pruebas automatizadas usan vectores de Bitcoin y un servidor Stratum local simulado. Permiten comprobar el cálculo y el intercambio del protocolo sin conectarse a ViaBTC ni ejecutar minería sostenida. La compilación cruzada genera un binario de Windows, pero no reemplaza comprobar visualmente la interfaz y las protecciones en un equipo Windows real.

La aceptación en el pool real depende también de la cuenta, la red, la dificultad y las políticas vigentes de ViaBTC. Una conexión abierta no demuestra que el trabajador esté autorizado; una autorización correcta tampoco garantiza que una CPU vaya a encontrar participaciones en un tiempo razonable.

La [arquitectura](docs/ARQUITECTURA.md) describe los componentes y los puntos de ampliación. La [guía de Windows](docs/GUIA-WINDOWS.md#comprobación-en-windows) contiene la comprobación manual de la aplicación.
