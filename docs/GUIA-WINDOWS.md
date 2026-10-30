# Guía de uso en Windows 11

## Preparación

Necesitas Windows 11 de 64 bits, una cuenta de ViaBTC para minería de Bitcoin y una conexión de red que permita llegar al servidor del pool. El ejecutable independiente incluye .NET; no necesitas instalarlo para usar el programa. El SDK solo es necesario para compilar el código.

La CPU compite con equipos ASIC especializados en Bitcoin. No esperes recuperar el coste de electricidad: una velocidad de cálculo positiva puede coexistir con cero participaciones aceptadas durante periodos muy largos.

En GitHub, abre **Actions**, selecciona una ejecución correcta de **Build and test Windows application** y descarga el artefacto **MineriaBitcoin-win-x64**. GitHub puede solicitar iniciar sesión para descargarlo. Extrae el paquete en una carpeta de tu usuario. El artefacto de cada ejecución tiene una retención de 90 días; el repositorio conserva el código para volver a compilar.

## Primera sesión

1. Abre `MineriaBitcoin.exe`. La minería estará detenida.
2. En el trabajador escribe `cuenta.trabajador`, usando tu cuenta de minería de ViaBTC y un nombre identificable para este equipo, por ejemplo `micuenta.pc01`.
3. Usa inicialmente `btc.viabtc.io` y el puerto `3333`. Si necesitas otra dirección o puerto, compruébalos primero en las instrucciones actuales de ViaBTC.
4. Conserva el objetivo de CPU en 25 % y un número reducido de hilos. Más hilos permiten utilizar más núcleos, pero no aseguran un mejor rendimiento dentro del mismo presupuesto de CPU.
5. Mantén activadas las opciones para pausar con batería y con el equipo ocupado.
6. Pulsa **Iniciar** y revisa el estado y el registro. El cliente primero conecta, se suscribe y autoriza el trabajador antes de empezar a calcular trabajos.

No introduzcas contraseñas de tu cuenta web, claves privadas ni frases de recuperación. El cliente utiliza la contraseña de trabajador `x`. La cartera de cobro, el método de pago, los mínimos y demás ajustes de cuenta se gestionan directamente en ViaBTC.

## Control de la sesión

- **Pausar** interrumpe el cálculo de la sesión iniciada. **Reanudar** permite continuarlo si las protecciones automáticas también lo permiten.
- **Detener** termina la sesión y su conexión al pool. Una sesión nueva requiere pulsar **Iniciar** otra vez.
- Minimizar mantiene abierta la aplicación y puede mantener la minería. Para dejar de minar, usa **Detener** o cierra la ventana.
- Cerrar la ventana finaliza el trabajo; el programa no se oculta como un minero residente ni se configura para iniciarse con Windows.
- Ajusta los parámetros con la sesión detenida y comienza de nuevo para usar los valores elegidos.

La pausa por carga se activa después de tres muestras con CPU global igual o superior a 85 %. Permite reanudar tras cinco muestras con CPU igual o inferior a 65 %. Esta diferencia evita alternar constantemente entre pausa y ejecución. La pausa manual permanece bajo tu control y una protección activa puede impedir la reanudación.

## Cómo interpretar los datos

| Dato | Qué significa |
| --- | --- |
| Velocidad de cálculo | Hashes calculados por segundo en el intervalo reciente; fluctúa con los descansos y las pausas. |
| Hashes totales | Trabajo de cálculo realizado por el motor durante la sesión. |
| Aceptadas | Participaciones que el pool confirmó como válidas. Es el dato relevante para saber si está aceptando trabajo. |
| Rechazadas | Participaciones que el pool rechazó. Revisa el registro si se repiten. |
| Pendientes | Participaciones enviadas que aún esperan respuesta. No equivalen a participaciones aceptadas. |
| CPU del minero | Consumo medido del proceso respecto a la capacidad total de CPU. |
| CPU del equipo | Carga global, incluyendo otros programas. |
| Duración | Tiempo transcurrido de la sesión; no representa tiempo ininterrumpido de cálculo. |

No hay estimación de ingresos. Una CPU puede estar conectada y calculando correctamente sin alcanzar la dificultad mínima del pool. También puede haber una demora antes de que ViaBTC muestre el trabajador en su panel.

## Consumo y temperatura

El objetivo de CPU, entre 5 % y 75 %, controla el tiempo de trabajo de los hilos mediante descansos. Su efecto depende del número de hilos, los núcleos disponibles y el plan de energía de Windows. Es una aproximación y pueden aparecer picos breves. La prioridad reducida permite al sistema dar preferencia a otras tareas.

El programa no mide temperatura ni velocidad de ventiladores. Si el equipo se calienta demasiado o responde mal, detén la sesión o reduce hilos y porcentaje. Revisa la refrigeración y las herramientas del fabricante cuando necesites supervisión térmica. La pausa por batería depende del estado de alimentación que Windows reporte.

## Configuración y registro

La configuración se guarda en:

```text
%LOCALAPPDATA%\MineriaBitcoin\settings.json
```

Contiene el nombre de trabajador y preferencias de conexión y consumo. No contiene claves privadas ni la contraseña de acceso web. Para trasladar las preferencias a otro equipo, cierra primero la aplicación y copia este archivo a la ubicación equivalente.

Usa la exportación del registro cuando necesites revisar un problema. El registro muestra actividad y errores del cliente, no los valores de autenticación. Antes de compartir un archivo revisa que el nombre del equipo o información añadida por el servidor sea apropiada para el destinatario.

## Problemas habituales

| Situación | Acción |
| --- | --- |
| No conecta o reconecta repetidamente | Comprueba servidor y puerto, conexión a Internet y reglas de firewall. El cliente reintenta con espera progresiva mientras la sesión permanezca iniciada. |
| Autorización rechazada | Confirma el formato `cuenta.trabajador`, el nombre de la cuenta de minería y las instrucciones de ViaBTC. No sustituyas `x` por tu contraseña web. |
| Conectado, sin velocidad | Revisa si está esperando un trabajo o está pausado por el usuario, por batería o por carga del sistema. |
| Hay velocidad pero no participaciones | Puede ser el resultado esperado de la dificultad de Bitcoin y el rendimiento de una CPU; no demuestra por sí solo un fallo de conexión. |
| Hay rechazos | Revisa los motivos del registro y la estabilidad de conexión. Trabajos antiguos pueden dejar de ser válidos cuando cambia el trabajo del pool. |
| El consumo es menor al objetivo | El número de hilos limita cuántos núcleos pueden trabajar. El objetivo es un techo aproximado de trabajo, no una obligación de consumir ese porcentaje. |

Los puertos `443` y `25`, si el pool los admite, son alternativas de transporte **TCP sin cifrado** en esta aplicación. Usar 443 no convierte la conexión en HTTPS ni TLS.

## Comprobación en Windows

La validación del código y del protocolo simulado no sustituye esta comprobación en el equipo donde lo usarás:

1. Abre el ejecutable y confirma que no conecta ni calcula hasta pulsar **Iniciar**.
2. Comprueba que los campos invalidan un trabajador vacío y valores fuera de rango.
3. Inicia una sesión con tu trabajador; comprueba autorización y llegada de trabajos en el registro.
4. Verifica que **Pausar**, **Reanudar** y **Detener** responden y que el uso de CPU desciende al detener.
5. Inicia de nuevo, minimiza y comprueba que puedes restaurar la ventana y detener la sesión.
6. Con una sesión activa, cierra la aplicación y confirma en el Administrador de tareas que no queda `MineriaBitcoin.exe` ejecutándose.
7. Si usas portátil, comprueba que desconectar la alimentación activa la pausa por batería y que una pausa manual no se pierde al volver a conectar.
8. Observa la pausa por carga cuando trabajes con otras aplicaciones y la recuperación cuando la carga descienda. No hace falta forzar una prueba térmica.
9. Cierra y vuelve a abrir: las preferencias deben conservarse y la minería debe seguir detenida hasta una nueva orden.

Si detectas un problema, registra la versión del ejecutable, los pasos y el mensaje visible, y conserva una copia del registro. Nunca incluyas credenciales de tu cuenta.
