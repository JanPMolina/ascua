# Mecánicas núcleo de Guidus

## 1. Controles y combate

- El mundo es una **cuadrícula**. Se mueve con flechas o deslizando el dedo arriba/abajo/izquierda/derecha (4 direcciones, sin diagonales).
- **Atacar = moverse hacia el enemigo** cuando está dentro del alcance del héroe (cuerpo a cuerpo = adyacente; arqueros/magos tienen más alcance).
- Interactuar con jarrones y cofres: moverse hacia ellos, **siempre adyacente** (sin importar el alcance).
- **Habilidad activa:** mantener presionada una dirección (o arrastrar largo) hasta llenar una barra y soltar. La dirección importa.
- Es **tiempo real**: los enemigos también se mueven casilla a casilla con sus propios ritmos; los ataques se **telegrafían** y el jugador esquiva.
- Estadísticas clave del héroe:
  - **Vida (corazones)**: valores enteros pequeños (1, 2, 3…).
  - **Escudo**: vida extra que se consume primero.
  - **Ataque**, **velocidad de ataque**, **velocidad de movimiento**.
  - **Probabilidad de bloqueo** (defensa probabilística: anula el golpe).
  - **Robo de vida** (probabilidad).
  - **Visión** (radio; hay niebla y enemigos que no te ven si no estás en su campo).
- Daños expresados como **% del ataque** (200%, 300%…). No hay decimales en las estadísticas visibles.

## 2. Bucle de juego

```
Campamento (hoguera) ──► elegir héroe + 1 de 2 bendiciones iniciales
      ▲                        │
      │                        ▼
  morir / ganar          Mazmorra: pisos ──► jefe de zona ──► …
      │                        │
      └── se pierden bendiciones; se conservan oro, piedras, artefactos, desbloqueos
```

- **Roguelite:** cada intento empieza de cero en poder temporal (bendiciones), pero lo permanente (oro, piedras, artefactos, héroes) se conserva.

## 3. Estructura de la mazmorra

Se **sube** desde el calabozo hasta el palacio:

1. **Calabozo** (obligatorio). Jefe aleatorio entre 4: Rey Slime, Rey Fantasma, Rey Rana, Liche.
2. **Puerta del Destino**: 3 puertas → elegir ruta (Sótano-Jardín, Alcantarilla, Campo de Entrenamiento). Al principio solo está abierta la del centro; las otras se desbloquean al llegar al palacio.
3. **Pasaje** de la ruta elegida. Jefe fijo: Comandante Caballero Negro.
4. **Centro de la ruta**. Jefes: Gorgona (alcantarilla), Guardián Antiguo (jardín), Caballero del Vacío (entrenamiento).
5. **Palacio**. Jefe final "???" en 3 fases.
6. **Abismo** (post-juego / modo difícil). Jefes: Faetón el Coloso Antiguo, y el Rey del Abismo en el abismo profundo.

### Pisos, cerraduras y llaves
- Cada piso tiene una **escalera gris con candado de cobre**. Se abre **matando a todos los monstruos** del piso o con una **llave de cobre** (que cae de cofres).
- **Templos**: en pisos aleatorios aparece una **escalera dorada con candado de plata**, que lleva al **Salón de las Bendiciones**. Solo se abre con **llave de plata** (en el abismo, con llave de oro; 5 de plata = 1 de oro).
  - Probabilidad aumentada por la bendición *Orbe de Luz* o por el nivel de mazmorra.
  - Dentro: bendición aleatoria y a veces el **Erudito de Monstruos** (tienda) o el **Chamán Oscuro**.

### Trampas (afectan también a los monstruos)
| Trampa | Comportamiento |
|---|---|
| Sierra | Se mueve lento por el suelo. No afecta a voladores (murciélagos) |
| Flechas | Disparadas por estatuas en línea recta; afectan a voladores |
| Monstruo espinoso | Explota si estás a 1 casilla (incluye diagonal) |
| Cristal abisal | Solo en el abismo; dispara en cruz periódicamente; no daña monstruos |
| Otros mencionados | Pantanos (ralentizan), hielo (deslizas), plataformas que caen, abismos (caída = muerte, ignora revivir) |

## 4. Progresión permanente

### Héroes
- Se empieza eligiendo **Espadachín, Maga de Fuego o Arquero**.
- Otros: 1.000 de oro o un anuncio → héroe aleatorio; **épicos** garantizados por 10.000.
- Niveles: hasta 5 se compra experiencia con oro; de 6 a 10 se gana matando monstruos (y requiere botín + oro). Los niveles 3/4/5 mejoran pasiva/activa (los valores entre barras `(a/b/c)` de [03-heroes.md](03-heroes.md)).
- **Estrellas** (logros por héroe): amarilla = nivel 5; celeste = terminar el palacio nivel 50; morada = ambas; roja = abismo hardcore.

### Piedras (estadísticas permanentes, compartidas entre todos los héroes)
8 tipos, máx. 50 por tipo (hasta 80 subiendo el nivel de mazmorra). Se encuentran en jarrones, cofres, rocas o en la tienda.

| Piedra | Efecto por unidad |
|---|---|
| Lucha | +0,2 ataque (se aplica cada 5) |
| Voluntad | +0,2% bloqueo |
| Agilidad | +0,3% vel. movimiento |
| Rapidez | +0,5% vel. ataque |
| Anhelo | +0,05% robo de vida |
| Valor | +1% daño a jefes |
| Abundancia | +2% oro |
| Paciencia | +0,2 escudo (se aplica cada 5) |

### Artefactos (compartidos, permanentes, mejorables hasta +20)
Se fabrican con **5 fragmentos** que sueltan los jefes + 1.000 de oro (con la Adivina).

| Fragmento (bonus suelto) | Artefacto | Efecto |
|---|---|---|
| Joya roja (Gorgona): +3 ataque | Tercer Ojo de la Gorgona | +5 ataque, 5% petrificar 1 s |
| Fragmento del Vacío (Caballero del Vacío): +5% movimiento | Cristal del Vacío | +10% movimiento, 10% onda de espada |
| Joya azul (Guardián): +1 escudo | Broche de Zafiro | 5% reflejar daño ×50 |
| Cristal de Luz (???): +5% bloqueo | Emblema Real | +10% bloqueo, 10% revivir con 2 de vida |

### Nivel de mazmorra (dificultad ajustable)
- Lo ajusta el Enano pagando **esencias de monstruo** + oro.
- Más nivel: más oro, más pisos especiales, mayor tope de piedras; pero los enemigos tienen más vida, ataque y bloqueo.
- Nivel 10: atajo para saltarse el calabozo.
- En el abismo puede llegar a 100.

### Bestiario
Matar varias veces a cada monstruo desbloquea por fases: silueta, luego nombre + barra de vida visible, luego +1 de daño contra él, luego más botín.

## 5. NPC del campamento

| NPC | Cómo se recluta | Función |
|---|---|---|
| Príncipe / Princesa | Inicio (se elige uno) | Vende el "tesoro real" (moneda premium / anuncios). El **Altar del Destino** permite cambiarlo por 10.000 |
| Chamán | Tras la primera partida | **Prohibir** bendiciones (3, luego 5), como un sistema de *bans*; desbloquear nuevas |
| Erudito de Monstruos | Tras el jefe del calabozo | Tienda: curación, esencias, barreras. También aparece en templos |
| Adivina | Tras la Gorgona | Fabrica y mejora artefactos |
| Enano | Tras el jefe del palacio | Ajusta el nivel de mazmorra |

## 6. Objetos de curación (dentro de la partida)
Zanahoria +1, carne +2, poción roja +3, hoja con puntos blancos (+1 vida máx. y actual), diamante azul +2 de escudo (único objeto de escudo).
