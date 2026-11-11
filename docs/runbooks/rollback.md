# Rollback de Ascua

**Estado: procedimiento preparado; prueba en Azure y duración real pendientes.** No se ha demostrado aún el requisito de menos de 5 minutos.

1. Abrir Actions → Rollback Azure → Run workflow, rama main.
2. Elegir dev/prod. En production debe existir la protección de aprobación; el workflow comprueba que no esté ausente.
3. Indicar la revisión sana del resumen del último despliegue. Dejar vacío selecciona la última revisión anterior a la que recibe más tráfico.
4. El script activa la revisión, verifica su `/health/ready`, cambia 100% del tráfico y verifica el dominio público. No borra revisiones, datos ni imágenes.
5. Verificar la reconexión de una partida en curso, historial y logs. El rollback de tráfico **no revierte migraciones de base de datos**. Requiere migraciones compatibles con la versión anterior; consultar a Jan antes de retroceder una versión con cambios incompatibles.

Para la evidencia, registrar enlace de ejecución, fecha, revisión anterior/nueva, segundos del resumen y resultado de una partida en curso. El script termina con error si dura 300 segundos o más. Si falla readiness, conserva el tráfico que ya estaba sirviendo.

| Prueba | Resultado |
|---|---|
| Ejecución | Pendiente |
| Tiempo medido | Pendiente |
| Continuidad de partida | Pendiente |
| Readiness y dominio público | Pendiente |

Si el pipeline no está disponible, usar la sesión del operador, ejecutar `bash infra/scripts/rollback.sh dev <revision>` (o prod) y conservar la misma evidencia. No usar `latest` para elegir la imagen ni desactivar la revisión que está sirviendo antes de confirmar readiness de la alternativa.
