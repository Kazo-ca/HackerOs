# ADR 0065 : presse-papiers de l'OS et partage de texte avec l'hôte

## Statut

Accepté le 2026-10-16.

- **Contexte :** décision validée par Benoit : un seul presse-papiers, propriété
  de l'OS, et un partage de texte avec la vraie machine « à la VirtualBox Guest
  Additions ». Demandée comme « ADR 0060 » ; ce numéro était déjà pris
  (ajustements d'administration système), d'où 0065.
- **Complète** l'ADR 0056 §12 (presse-papiers de fichiers), sans le contredire :
  `HackClipboard`, le format `x-special/gnome-copied-files` et les capacités
  `clipboard.read` / `clipboard.write` restent tels quels.
- **Remplace** l'ADR 0060 §5 : Diagnostic ne copie plus par `BrowserClipboard`
  mais par `HackClipboard` (il déclare donc de nouveau `clipboard.write`), et
  `BrowserClipboard` est marqué `[Obsolete]` (ADR 0019).

## Contexte

Le chantier `rebuild/4-file-explorer` a livré `SystemClipboard`, une sélection
par session dans Platform Core, partagée par toutes les apps de la session.
Deux manques restaient :

- le texte copié dans HackerOS n'arrivait pas dans le presse-papiers de la vraie
  machine, sauf par `BrowserClipboard`, une sortie directe qui contournait le
  presse-papiers de l'OS et toute politique ;
- un collage depuis la vraie machine n'entrait pas dans le presse-papiers de
  l'OS : une app qui lisait `HackClipboard` ne voyait pas ce que l'utilisateur
  venait de copier ailleurs, et des fichiers copiés sur l'hôte ne se collaient
  pas dans l'explorateur.

Un vrai système invité sous VirtualBox règle ce problème par un service des
Guest Additions qui recopie le texte dans les deux sens, réglable
(« bidirectionnel », « invité vers hôte », « hôte vers invité », « désactivé »).

## Décision

### 1. Un seul presse-papiers, propriété de l'OS

- `SystemClipboard` (Platform Core) reste l'unique presse-papiers : une entrée
  par session (`SystemClipboardEntry`), en mémoire seulement, perdue au
  redémarrage et vidée à la déconnexion.
- L'entrée est **multi-formats** : `text/plain`, `x-special/gnome-copied-files`
  (ADR 0056 §12), et tout autre type MIME par `SetTargets` (extensible).
- Chaque changement reçoit un **numéro de séquence** par session
  (`GetSequenceNumber`, comme `GetClipboardSequenceNumber` de Windows) et lève
  l'événement `Changed`.
- L'entrée note son **origine** (`SystemClipboardOrigin`) : `Guest` (une app par
  `HackClipboard`), `GuestNative` (copie native dans un champ de texte ou le
  terminal), `Host` (collage depuis la vraie machine).
- Les apps n'y accèdent que par `HackClipboard` et `IAppClipboardGateway`, avec
  `clipboard.read` / `clipboard.write`. L'ancienne classe en mémoire du SDK
  (`AppClipboardGateway` non partagée) a déjà été remplacée par la passerelle
  de Platform Core vers ce service ; rien ne change pour les apps.

### 2. Sortie vers l'hôte

- Quand une app met une entrée contenant du `text/plain`, le pont
  `HostClipboardBridge` le recopie par `navigator.clipboard.writeText`. L'appel
  part pendant l'activation transitoire du geste qui a copié (Ctrl+C, clic) :
  le navigateur ne demande pas d'autorisation.
- Une liste de fichiers sort sous forme de chemins texte (sa cible
  `text/plain`), jamais sous son format GNOME.
- Un refus du navigateur (pas de focus, pas d'API) est journalisé ; le
  presse-papiers de l'OS n'est pas touché.

### 3. Entrée depuis l'hôte

- Le module écoute l'événement `paste` sur le document, en phase de capture,
  avant toute app. `clipboardData` est lisible sans autorisation.
- Si le texte collé diffère du dernier texte que l'OS sait présent sur l'hôte
  (écrit par lui ou déjà reçu), il devient l'entrée courante, origine `Host`.
  Il n'est jamais renvoyé à l'hôte.
- `navigator.clipboard.readText` n'est **pas** appelé sur le chemin normal (il
  demanderait une autorisation).
- Les fichiers collés (`clipboardData.files`) sont notés par nom dans la cible
  `application/x-hackeros-host-files`, et la `FileView` qui reçoit le collage
  les importe dans le dossier courant par le même chemin que le dépôt de
  fichiers de l'hôte (`DropRequested`, puis `FileTransfer.ImportAsync`). Pour
  cela, Ctrl+V dans une `FileView` suit l'événement `paste`, qui arrive après
  la mise à jour du presse-papiers, et non plus `keydown` (avec un repli de
  250 ms pour les navigateurs qui ne lèvent pas `paste` hors d'un champ).

### 4. Réglage `/etc/hackeros/clipboard.config`

- Clé `sharing` : `bidirectional` (défaut), `guest-to-host`, `host-to-guest`,
  `disabled`. Document en couches de l'ADR 0044 : défaut machine dans
  `/etc/hackeros/clipboard.config`, surcharge dans
  `~/.config/hackeros/clipboard.config`, verrouillable par `[locked]`.
- Le nom suit la convention des documents de l'OS (`<document>.config`, ADR
  0044), et non `clipboard.conf` comme dans la demande.
- Le fichier est installé commenté au premier démarrage et listé par
  `OsSettingsDocumentRegistry` (Paramètres peut l'ouvrir par
  `HackSettings.OpenSystemAsync`). Un changement s'applique sans redémarrer.
- Le sens refusé est appliqué dans la page, pendant l'événement :
  - hôte vers invité refusé, ou texte de l'hôte périmé : un collage dans un
    champ de texte de HackerOS reçoit le texte du presse-papiers de l'OS à la
    place de celui de l'hôte (`insertText`), et les fichiers ne sont pas
    importés ;
  - invité vers hôte refusé : une copie native ne modifie pas le presse-papiers
    de l'hôte, et le pont n'écrit rien.

### 5. Pas de chiffrement

Les données internes ne sont pas chiffrées : la clé serait forcément dans le
WASM, donc lisible. À la place, elles ne quittent jamais la mémoire de l'OS ;
seul le texte part vers l'hôte, et seulement si le réglage le permet.

### 6. JavaScript minimal

Un module `hostClipboard.js` dans le `wwwroot/` de
`HackerOs.Infrastructure.Browser`, importé par `BrowserHostClipboardChannel`
(contrat `IHostClipboardChannel` dans Simulation.Abstractions). Aucun hôte ne
le référence. La logique (origines, sens, séquence, session) reste en C# dans
`HostClipboardBridge`, testable sans navigateur ; le module ne garde que l'état
nécessaire pour décider de façon synchrone pendant l'événement.

## Conséquences

- Le texte copié par n'importe quelle app (Diagnostic, explorateur, éditeur)
  arrive sur la vraie machine, et un collage de la vraie machine est visible
  par `HackClipboard.GetTextAsync`.
- Une copie native dans un champ de HackerOS devient aussi l'entrée courante,
  comme sur un vrai bureau.
- Au premier collage après le démarrage, l'OS ne connaît pas encore le contenu
  de l'hôte : un texte jamais vu est pris comme nouveau. Avec `bidirectional`,
  c'est sans effet visible (l'OS a déjà écrit son texte sur l'hôte) ; avec
  `host-to-guest`, ce premier collage peut remplacer une liste de fichiers
  coupée dans l'OS. C'est le comportement « dernier changement gagnant » de
  VirtualBox.
- Diagnostic déclare `clipboard.write` (capacité normale).
- `BrowserClipboard` reste une version, `[Obsolete]`, puis sera retiré.

## Alternatives écartées

- **Lire l'hôte par `navigator.clipboard.readText`** à chaque collage ou à
  intervalles : demande une autorisation (Firefox, Safari), et la lecture en
  arrière-plan révélerait à l'OS ce que l'utilisateur copie ailleurs sans geste
  de sa part.
- **Chiffrer le presse-papiers en mémoire** : aucune protection réelle, la clé
  étant dans le même processus.
- **Garder `BrowserClipboard` comme sortie officielle** : deux presse-papiers,
  et un réglage `sharing` contournable par n'importe quelle app.
- **Tout décider en C#** : un collage ou une copie ne peuvent être modifiés que
  pendant leur distribution, de façon synchrone ; un aller-retour .NET arrive
  trop tard.

## Références

- ADR 0019 (versionnage du SDK), ADR 0044 (paramètres en fichiers, `[locked]`),
  ADR 0055 (frontière du SDK), ADR 0056 §12, ADR 0058 §1, ADR 0060 §5.
- [`clipboard.md`](../clipboard.md), [`file-explorer.md`](../file-explorer.md).
