# ADR 0134 : connexion automatique au serveur qui sert l'OS, et icône d'état dans la zone de notification

## Statut

Accepté le 2026-10-18 (réponses de Benoit aux questions E1 à E4 du fil
« ADR des suites de l'étape 5 »).

- **Remplace :** l'[ADR 0130](0130-server-serves-webassembly-os-and-api.md)
  §4 (adresse préremplie, connexion au choix de l'utilisateur).
- **Confirme :** l'ADR 0130 §2 (E1 : le serveur sert les fichiers publiés de
  l'Ecosystem), l'ADR 0125 §6 (E3 : valeurs Linux pour `nofile` et `nproc`),
  l'ADR 0132 §2 (E4 : SDK 1.2 publié une fois, à la fin de la série).
- **Mis en œuvre par :** `exec/8-server-wasm`.

## Contexte

Benoit a choisi la réponse A à E2, en la précisant : « connexion
automatique, sans demande de l'utilisateur. Une icône dans la zone de
notification affiche l'état de la connexion ».

## Décision

### 1. Connexion sans question

- Au démarrage, l'OS interroge `GET /api/hackeros/host-info` sur sa propre
  origine (délai court, rien hors ligne). Si le serveur répond, l'OS
  l'enregistre comme serveur de la machine et **s'y connecte tout de suite**,
  sans boîte ni question.
- Se connecter veut dire : vérifier la version de l'API, ouvrir le canal de
  synchronisation et le relais. Si l'utilisateur de la session a déjà ouvert un
  compte sur ce serveur (jeton gardé, ADR 0028), la session reprend aussi
  sans question. Sinon, l'OS est connecté **sans compte** : ce qui demande un
  compte attend que l'utilisateur s'identifie depuis l'icône (§2).
- Un serveur perdu en route se retente avec un délai croissant. L'OS ne dépend
  jamais du serveur (AGENTS.md I.1) : déconnecté, il continue hors ligne.
- Un administrateur peut couper ce comportement dans
  `/etc/hackeros/server.config` (`auto-connect=false`) ; l'OS revient alors à
  la saisie manuelle dans Paramètres.

### 2. Icône d'état

- Une icône dans la zone de notification de la barre des tâches, visible dès
  qu'un serveur est configuré (automatiquement ou à la main, Ecosystem
  compris).
- États : **Connecté**, **Connecté, non identifié**, **Connexion…**,
  **Hors ligne** (serveur injoignable), **Erreur** (version incompatible, accès
  refusé). Infobulle avec l'adresse du serveur et l'état.
- Un clic ouvre un petit panneau : état, adresse, compte, dernière
  synchronisation, boutons « S'identifier » (si non identifié), « Reconnecter »
  et « Paramètres du serveur ».
- Composant de la plateforme en Blazor natif avec CSS isolé (barre des
  tâches, AGENTS.md II.1) ; textes en anglais et en français (ADR 0104) ;
  `aria-live` pour les changements d'état et navigation au clavier
  (ADR 0105).

## Conséquences

- `HackerOs.Server` gagne `GET /api/hackeros/host-info` (version de l'API,
  nom du serveur) ; l'OS gagne la détection et l'icône.
- Tests : boot servi par le serveur → connexion sans boîte ; serveur arrêté →
  icône « Hors ligne », OS utilisable ; `auto-connect=false` respecté.
- Docs : [`server-network-relay.md`](../server-network-relay.md),
  [`project-overview.md`](../project-overview.md) et la doc de connexion au
  serveur, au moment du chantier.

## Références

- ADR 0028, 0104, 0105, 0125, 0130, 0132.
