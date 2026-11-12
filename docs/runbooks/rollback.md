# Rollback de Ascua

**Verificado en dev el 12-nov-2026: 53 segundos**, por debajo de los 5 minutos. [Ejecución y logs](https://github.com/JanPMolina/ascua/actions/runs/43299999681).

1. Abrir Actions → Rollback Azure → Run workflow, rama main.
2. Elegir dev/prod. En production debe existir la protección de aprobación; el workflow comprueba que no esté ausente.
3. Indicar la revisión sana del resumen del último despliegue. Dejar vacío selecciona la última revisión anterior a la que recibe más tráfico.
4. El script activa la revisión, verifica su `/health/ready`, cambia 100% del tráfico y verifica el dominio público. No borra revisiones, datos ni imágenes.
5. Verificar la reconexión de una partida en curso, historial y logs. El rollback de tráfico **no revierte migraciones de base de datos**. Requiere migraciones compatibles con la versión anterior; consultar a Jan antes de retroceder una versión con cambios incompatibles.

Para cada ejecución, registrar enlace, fecha, revisiones, segundos y resultado de una partida en curso. El script termina con error si dura 300 segundos o más. Si falla readiness, conserva el tráfico que ya estaba sirviendo.

| Prueba | Resultado |
|---|---|
| Ejecución | [43299999681](https://github.com/JanPMolina/ascua/actions/runs/43299999681), 12-nov-2026 20:25 UTC |
| Revisión restaurada | `ca-ascua-dev--47jm38q` |
| Tiempo medido | **53 segundos** |
| Readiness y dominio público | Ambos HTTP 200 |
| Continuidad durante este rollback | No se mantuvo una partida activa; no atribuir esta garantía a la sonda HTTP |

La continuidad se verificó por separado durante el [despliegue automático 43301113236](https://github.com/JanPMolina/ascua/actions/runs/43301113236): dos clientes web reales conservaron la partida, héroes y vida desde la ronda 1 hasta la 15, siguieron conectados por MessagePack y recibieron ACK ACCEPTED de una nueva orden tras finalizar el despliegue (20:36:38 UTC). Esta observación no sustituye las pruebas automatizadas de pérdida o duplicación de órdenes.

Producción permanece bloqueada hasta que Jan configure los revisores nativos del entorno. Si el pipeline no está disponible, usar la sesión del operador, ejecutar `bash infra/scripts/rollback.sh dev <revision>` (o prod) y conservar la misma evidencia. No usar `latest` para elegir la imagen ni desactivar la revisión que está sirviendo antes de confirmar readiness de la alternativa.
