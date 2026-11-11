# Costos de Ascua

Estimación preparada el **11-nov-2026**, eastus2, USD, sin impuestos ni descuentos. El portal Education mostró **USD 100/100** disponibles, vencimiento **11-nov-2027**. Usar únicamente la suscripción Azure for Students. La revisión es el **19-nov**; disponibilidad solicitada **hasta el 22-nov inclusive**.

## Tarifas y supuestos

Consulta a la [API oficial de precios](https://prices.azure.com/api/retail/prices), filtrada por eastus2 y tipo Consumption. No se contratan reservas. Mes de referencia: 730 h; período conservador de 12 días: 288 h.

| Recurso | Dev, USD/mes | Prod, USD/mes | Supuesto |
|---|---:|---:|---|
| ACR Basic | 5,07 | 5,07 | 0,1666/día; almacenamiento extra no incluido |
| Postgres B1ms | 12,41 | 12,41 | 0,017/h, encendido siempre (sin descontar beneficios gratuitos) |
| Postgres 32 GB | 3,68 | 3,68 | 0,115 por GB/mes; backup excedente a 0,095/GB/mes |
| Managed Redis B0 | 11,68 | 23,36 | 0,016 por instancia/h; estimación de dos instancias para HA en prod, confirmar presupuesto del portal |
| Container Apps | 1,30 | 78,84 | Dev: 24 h/mes de una réplica activa. Prod: dos réplicas de 0,5 vCPU/1 GiB activas todo el mes |
| Logs + Insights | hasta ~8,56 | hasta ~8,56 | 0,1 GB/día por workspace y referencia 2,76/GB; confirmar tarifa aplicada; exceso del cap y otras tablas pueden añadir costo |
| Alertas, Key Vault y margen | 5,00 | 5,00 | Reserva orientativa; comprobar desglose tras desplegar |
| **Total de referencia** | **47,70** | **136,92** | **184,62/mes entre ambos**, antes de franquicia gratuita de Container Apps |

Container Apps activo: 0,000024/vCPU-segundo y 0,000003/GiB-segundo; una réplica cuesta hasta 0,054/h. Existe una franquicia mensual por suscripción de 180.000 vCPU-segundos y 360.000 GiB-segundos; no se duplicó entre ambientes. No se usan perfiles dedicados, endpoints privados ni sesiones dinámicas, que tienen otros cargos.

**Hasta el 22-nov:** este escenario da aproximadamente **USD 73** para 12 días; reservar hasta **USD 90** para carga, despliegues con revisiones solapadas, alertas, red y variaciones de medición. Es una estimación, no una garantía: más tráfico, cuatro réplicas sostenidas o un incremento del cap pueden agotar el crédito antes. Comprobar las tarifas y el crédito en el portal antes de crear recursos. Los beneficios gratuitos de Postgres/ACR pueden reducir el consumo si la suscripción los tiene habilitados; no se presupone.

## Controles operativos

- Crear presupuesto mensual de USD 90 con avisos al 50%, 80% y 100%, y revisar también el saldo de Education. **Un presupuesto avisa, no detiene servicios automáticamente.** No convertir la suscripción a pago por uso.
- Mantener dev en minReplicas=0 y finalizar bots/partidas al concluir las pruebas. Detener Postgres dev cuando no se use (`az postgres flexible-server stop`); Azure lo reinicia automáticamente tras el plazo máximo admitido, por lo que hay que revisar su estado.
- Redis sigue facturando mientras exista. Eliminarlo pierde datos de caché y requiere autorización y plan de reconstrucción; no se programa automáticamente.
- Mantener prod con al menos dos réplicas durante el período de revisión; no apagarlo el 19 porque el usuario pidió disponibilidad hasta el 22.
- Al finalizar el 22, acordar exportación/conservación de datos y apagado. La etiqueta reviewUntil es informativa y **no elimina ni apaga** recursos.
- Registrar consumo real y saldo antes/después de la prueba de carga; completar esta estimación con el precio confirmado del portal y evidencia de presupuesto.

Fuentes: [Container Apps: facturación y franquicia](https://learn.microsoft.com/en-us/azure/container-apps/billing), [precios Container Apps](https://azure.microsoft.com/en-us/pricing/details/container-apps/), [precios Managed Redis](https://azure.microsoft.com/en-us/pricing/details/managed-redis/), [precios Postgres](https://azure.microsoft.com/en-us/pricing/details/postgresql/flexible-server/).
