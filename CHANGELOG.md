# Cambios

## 0.1.0

- Primera aplicación Windows x64 en español para minería Bitcoin CPU con ViaBTC.
- Inicio, pausa y parada manuales; cierre de ventana detiene el proceso.
- Objetivo de CPU configurable, prioridad baja, pausa por batería o carga del equipo y una sola instancia por sesión de Windows.
- SHA-256d, Stratum V1, reconexión, contadores reales y registro acotado.
- Quince pruebas de cálculo, límites de colas, cancelación y protocolo contra un pool simulado.
- Compilación independiente de .NET para Windows y código modular para futuras mejoras.

Validado en este entorno: compilación cruzada Windows x64 y 15 pruebas del motor/protocolo. Pendiente de comprobar en un equipo Windows real: interfaz, sensores de carga/alimentación y conexión/autorización con una cuenta ViaBTC. No se ha demostrado una share aceptada por ViaBTC. No incluye firma de editor ni lectura de temperatura.
