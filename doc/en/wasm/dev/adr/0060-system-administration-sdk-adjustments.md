# ADR 0060 : Ajustements de l'API d'administration du système

## Statut

Accepté le 2026-10-11.

- **Phase du plan :** phase 4 — SDK complet et apps de base, chantier
  `rebuild/4-sdk-system-admin` (ADR 0058).
- **Remplace en partie :** ADR 0056, sections 8, 9 et 11, sur les points
  listés ci-dessous. Le reste de l'ADR 0056 ne change pas.

## Contexte

L'ADR 0056 décrit `HackSettings.OpenSystemAsync`, `HackPermissionAdministration`,
`HackServerConnection`, `HackSystem` et `HackStorageDiagnostics`. En les
écrivant, cinq points de l'ADR ne tenaient pas tels quels :

1. La passerelle de `HackSystem` s'appelle `IAppSystemGateway System`. Une
   propriété `System` sur `IAppExecutionContext` masque l'espace de noms
   `System` dans toute classe qui implémente l'interface (`System.IO`,
   `System.Text.Json`… ne se résolvent plus sans `global::`).
2. `HackPermissionAdministration.SetMachineDecisionAsync` prend une décision
   `bool?` (`null` = « redemander au lancement »). Un `null` qui veut dire
   quelque chose se lit mal dans l'appel et dans les journaux.
3. L'ADR ne dit pas qui écrit les couches d'un document de l'OS. La couche
   `/etc/hackeros` appartient à root ; la couche `~/.config/hackeros` à
   l'utilisateur.
4. Paramètres change les quatre clés d'apparence d'un coup. Avec une écriture
   par clé, la couche système demanderait quatre approbations.
5. Diagnostic copie le JSON « par `IAppClipboardGateway` ». Ce
   presse-papiers est celui de la session simulée (ADR 0056 §12) : le JSON ne
   sort pas de HackerOS, alors que l'export sert à le coller dans un rapport de
   bogue, hors de la simulation.

## Décision

1. **`IAppSystemPowerGateway SystemPower`** remplace `IAppSystemGateway System`
   sur `IAppExecutionContext`. La façade publique reste `HackSystem`.
2. **`HackPermissionDecision { Allow, Deny, Ask }`** remplace `bool?`. `Ask`
   retire la décision machine.
3. **Écritures d'un document de l'OS :**
   - la couche utilisateur est écrite par l'OS au nom de l'utilisateur de la
     session (même sous `sudo`, c'est sa couche, pas celle de root) ;
   - la couche système est écrite avec les droits de fichiers **de l'app
     appelante** : `sudo`, ou l'approbation administrateur de l'ADR 0042 ;
     un refus donne `UnauthorizedAccessException` ;
   - les documents ouverts sont listés par `OsSettingsDocumentRegistry`
     (`appearance`, `ui-platform-preference`, `security`, `policy`). La
     politique et les décisions machine restent réservées à
     `HackPermissionAdministration`, qui passe par l'agent d'authentification.
4. **`HackSettingsDocument.SetManyAsync(values, layer)`** écrit plusieurs clés
   d'une couche en une seule édition du fichier, donc une seule approbation.
5. **Diagnostic copie dans le presse-papiers de l'hôte** par
   `BrowserClipboard` (`HackerOs.AppSdk.Blazor`, module `clipboard.js`), et
   propose aussi « Télécharger le JSON » et « Enregistrer dans HackerOS ». Il
   ne déclare plus `clipboard.write`.

## Conséquences

- Diagnostic reste la seule app non système de la base à déclarer une
  capacité dangereuse (`system.storage.inspect`) : son premier lancement
  demande un administrateur, comme le voulait l'ADR 0056. Le test
  `No_non_system_app_of_the_host_declares_a_dangerous_capability` le nomme
  comme exception.
- `IAppExecutionContext` reçoit cinq membres par défaut (`SystemSettings`,
  `PermissionAdministration`, `ServerConnection`, `SystemPower`,
  `StorageDiagnostics`) ; une implémentation qui ne les fournit pas lève
  `NotSupportedException` (`UnsupportedAdministrationGateways`).
- La liste d'exemptions de la frontière du SDK est vide après ce chantier.

## Références

- ADR 0042 (`sudo` et approbation), ADR 0044 (paramètres en fichiers), ADR
  0049 (capacités dangereuses), ADR 0055 §5 (agent d'authentification), ADR
  0056 §8 à §12.
- [`system-administration.md`](../../framework/system-administration.md).
