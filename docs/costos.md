# Costos de Ascua

Estimación preparada el **11-nov-2026**, westus2 (PostgreSQL en northcentralus), USD, sin impuestos ni descuentos. El portal Education mostró **USD 100/100** disponibles, vencimiento **11-nov-2027**. Usar únicamente la suscripción Azure for Students. La revisión es el **19-nov**; disponibilidad solicitada **hasta el 22-nov inclusive**.

## Tarifas y supuestos

Consulta a la [API oficial de precios](https://prices.azure.com/api/retail/prices), filtrada por westus2 y tipo Consumption. No se contratan reservas. Mes de referencia: 730 h; período conservador de 12 días: 288 h.

| Recurso | Dev, USD/mes | Prod, USD/mes | Supuesto |
|---|---:|---:|---|
| ACR Basic | 5,07 | 5,07 | 0,1666/día; almacenamiento extra no incluido |
| Postgres B1ms | 12,41 | 12,41 | 0,017/h, encendido siempre (sin descontar beneficios gratuitos) |
| Postgres 32 GB | 3,68 | 3,68 | 0,115 por GB/mes; backup excedente a 0,095/GB/mes |
| Managed Redis B0 | 11,68 | 23,36 | 0,016 por instancia/h; estimación de dos instancias para HA en prod, confirmar presupuesto del portal |
| Container Apps | 1,81 | 110,38 | Dev: 24 h/mes de una réplica activa. Prod: dos réplicas de 0,5 vCPU/1 GiB activas todo el mes |
| Logs + Insights | hasta ~8,56 | hasta ~8,56 | 0,1 GB/día por workspace y referencia 2,76/GB; confirmar tarifa aplicada; exceso del cap y otras tablas pueden añadir costo |
| Alertas, Key Vault y margen | 5,00 | 5,00 | Reserva orientativa; comprobar desglose tras desplegar |
| **Total de referencia** | **48,21** | **168,46** | **216,67/mes entre ambos**, antes de franquicia gratuita de Container Apps |

Container Apps activo en West US 2: 0,000034/vCPU-segundo y 0,000004/GiB-segundo; una réplica cuesta hasta 0,0756/h. Existe una franquicia mensual por suscripción de 180.000 vCPU-segundos y 360.000 GiB-segundos; no se duplicó entre ambientes. No se usan perfiles dedicados, endpoints privados ni sesiones dinámicas, que tienen otros cargos.

**Hasta el 22-nov:** este escenario da aproximadamente **USD 86** para 12 días; reservar hasta **USD 95** para carga, despliegues con revisiones solapadas, alertas, red y variaciones de medición. Es una estimación, no una garantía: más tráfico, cuatro réplicas sostenidas o un incremento del cap pueden agotar el crédito antes. Comprobar las tarifas y el crédito en el portal antes de crear recursos. Los beneficios gratuitos de Postgres/ACR pueden reducir el consumo si la suscripción los tiene habilitados; no se presupone.

La región inicial eastus2 fue rechazada durante la validación por RequestDisallowedByAzure. La directiva de la suscripción permitió westus2, brazilsouth, chilecentral, belgiumcentral y northcentralus; se eligió West US 2 para la plataforma. PostgreSQL está restringido allí para esta suscripción: solo la base usa North Central US, con B1ms a 0,017/h y almacenamiento a 0,115/GB-mes confirmados en la API oficial el 11-nov. La transferencia entre regiones usa parte del margen; medirla con el consumo real. El precio de Container Apps allí es mayor que en East US 2 y esta tabla ya usa la tarifa corregida.

## Controles operativos

- Presupuesto mensual **ascua-noviembre**, USD 90, creado el 11-nov en la suscripción estudiantil con avisos al 50%, 80% y 100% al correo del operador. Revisar también el saldo de Education. **Un presupuesto avisa, no detiene servicios automáticamente.** No convertir la suscripción a pago por uso. Límite de consumo autorizado por el usuario para esta tarea: **USD 95 del crédito**.
- Mantener dev en minReplicas=0 y finalizar bots/partidas al concluir las pruebas. Detener Postgres dev cuando no se use (`az postgres flexible-server stop`); Azure lo reinicia automáticamente tras el plazo máximo admitido, por lo que hay que revisar su estado.
- Redis sigue facturando mientras exista. Eliminarlo pierde datos de caché y requiere autorización y plan de reconstrucción; no se programa automáticamente.
- Mantener prod con al menos dos réplicas durante el período de revisión; no apagarlo el 19 porque el usuario pidió disponibilidad hasta el 22.
- Al finalizar el 22, acordar exportación/conservación de datos y apagado. La etiqueta reviewUntil es informativa y **no elimina ni apaga** recursos.
- Registrar consumo real y saldo antes/después de la prueba de carga; completar esta estimación con el precio confirmado del portal y evidencia de presupuesto.

Fuentes: [Container Apps: facturación y franquicia](https://learn.microsoft.com/en-us/azure/container-apps/billing), [precios Container Apps](https://azure.microsoft.com/en-us/pricing/details/container-apps/), [precios Managed Redis](https://azure.microsoft.com/en-us/pricing/details/managed-redis/), [precios Postgres](https://azure.microsoft.com/en-us/pricing/details/postgresql/flexible-server/).

## Región de producción y cuota observada

Esta suscripción solo permite un Container Apps Environment por región. Dev ocupa West US 2, por lo que prod queda parametrizado en North Central US (incluido PostgreSQL). La tabla anterior conserva las tarifas de referencia de West US 2, con PostgreSQL de North Central US al mismo precio verificado. Recalcular la cotización de prod en North Central US antes de habilitarlo; no se activa producción mientras falten la aprobación nativa y esa comprobación. Se conserva el máximo autorizado de USD 95.
