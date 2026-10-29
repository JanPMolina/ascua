# Planteamiento: Proyecto 2 (servidor de juego por turnos en Azure)

Decisiones tomadas (29-oct-2026):
- **Juego:** semi-cooperativo, por turnos simultáneos, inspirado en Guidus pero con reglas propias.
- **Stack:** C# / .NET con SignalR.
- **Equipo:** 2 personas.
- **Cliente de prueba:** web mínimo.

| Documento | Contenido |
|---|---|
| [01-juego.md](01-juego.md) | Reglas de **Ascua**: ronda, órdenes, ascuas, pactos secretos, héroes, bendiciones, enemigos, información oculta |
| [02-arquitectura.md](02-arquitectura.md) | Capas, flujo de ronda, concurrencia, seguridad, Azure, observabilidad, rendimiento, ADRs |
| [03-plan-de-trabajo.md](03-plan-de-trabajo.md) | Roles A/B, cronograma por fases hasta el 12-nov, recortes y riesgos |

Inspiración: [../inspiracion](../inspiracion/README.md) · Enunciado: [../Proyecto3.pdf](../Proyecto3.pdf)
