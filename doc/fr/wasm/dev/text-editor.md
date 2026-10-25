> [English version](../../../en/wasm/dev/text-editor.md)

# Éditeur de texte

App système `HackerOs.Apps.TextEditor` (`org.hackeros.text-editor`). Ce document
décrit son fonctionnement actuel et la recherche avec remplacement ajoutée par le
chantier `rebuild/4-text-editor` (phase 4, [ADR 0058](adr/0058-base-app-features.md) §3).

## Rôle

L'éditeur ouvre, modifie et enregistre des fichiers texte du système de fichiers
virtuel, avec contrôle de version optimiste à l'écriture. Il passe uniquement par
le SDK public ([`app-sdk.md`](../framework/app-sdk.md)) et n'a plus d'exemption à la frontière
du SDK depuis `rebuild/4-sdk-user-and-apps`.

## Architecture

| Élément | Rôle |
| --- | --- |
| `TextEditorApp` | Manifeste `[AppManifest]` : capacités `apps.launch`, lecture et écriture du dossier personnel, boîtes de dialogue Ouvrir/Enregistrer, notifications ; gestionnaire de fichiers `text/plain`. |
| `TextEditorDocument` | État d'un document : chemin, contenu, type MIME, état de chargement, modifié ou non, révision lue. |
| `TextEditorFindEngine` | Logique de recherche et de remplacement, sans Blazor ni JavaScript, testée seule (`Tests/HackerOs.Apps.TextEditor.Tests`). |
| `TextEditorWindow.razor` | Fenêtre : menus, barre de recherche et de remplacement, zone de texte, barre d'état, raccourcis clavier. |
| `TextEditorWindow.razor.js` | Ce que C# n'atteint pas : sélection dans la zone de texte (`selectRange`, `getSelectionRange`) et blocage des raccourcis du navigateur (`guardEditorShortcuts`). |

### Ouvrir et enregistrer

- Extensions ouvertes : `.txt`, `.log`, `.conf`, `.json`, `.md` ; fichiers binaires
  et fichiers de plus de 512 Kio refusés.
- Hors du dossier personnel, l'accès vient du jeton remis par la boîte de dialogue
  (`WithSelectedHandle`).
- L'enregistrement envoie la révision lue. Si le fichier a changé entre-temps
  (`RevisionConflict`), l'éditeur signale le conflit et propose « Save As ».

## Rechercher et remplacer (ADR 0058 §3)

| Raccourci | Action |
| --- | --- |
| Ctrl+F | Ouvre la barre de recherche seule ; la referme si elle est déjà dans ce mode. |
| Ctrl+H | Ouvre la barre avec la ligne « Replace with » ; la referme si elle est déjà dans ce mode. |
| Entrée / Maj+Entrée (recherche) | Occurrence suivante / précédente, avec retour au début. |
| Entrée (remplacement) | « Replace ». |
| Ctrl+Alt+Entrée (remplacement) | « Replace All ». |
| Échap | Ferme la barre. |

Le chevron ▸/▾ à gauche affiche ou masque la ligne de remplacement sans fermer la
barre. Le menu « Edit » propose aussi « Find » et « Replace ».

- **Options partagées** par la recherche et le remplacement : « Aa » (respecter la
  casse) et « ab| » (mot entier : l'occurrence ne doit pas être collée à une
  lettre, un chiffre ou `_`). Pas d'expressions régulières dans cette phase.
- **Replace** remplace l'occurrence sélectionnée dans la zone de texte si elle
  correspond à la recherche, puis sélectionne la suivante (après le texte inséré,
  pour qu'un remplacement qui contient la recherche ne soit pas repris). Sinon il
  sélectionne seulement l'occurrence suivante : un deuxième clic la remplace.
- **Replace All** remplace toutes les occurrences en une seule modification du
  document et affiche leur nombre (« 12 replacements »).
- Un remplacement est une modification ordinaire : le document devient modifié et
  l'enregistrement garde la détection des conflits.
- Le navigateur ne reçoit plus Ctrl+S, Ctrl+O, Ctrl+N, Ctrl+F ni Ctrl+H quand le
  focus est dans l'éditeur (Ctrl+H ouvrait l'historique de Chrome).

### API de `TextEditorFindEngine`

```csharp
var options = new TextEditorFindOptions(MatchCase: true, WholeWord: false);

List<int> matches = TextEditorFindEngine.FindMatches(content, "cat", options);
int next = TextEditorFindEngine.FindNextFrom(content, "cat", fromOffset: 10, options);

TextEditorReplaceResult step = TextEditorFindEngine.Replace(
    content, "cat", "dog", selectionStart, selectionLength, options);
// step.Content, step.Replaced, step.NextMatchStart (-1 s'il n'en reste plus)

TextEditorReplaceAllResult all = TextEditorFindEngine.ReplaceAll(content, "cat", "dog", options);
string summary = TextEditorFindEngine.DescribeReplacementCount(all.Count, L); // "12 replacements", « 12 remplacements »
```

`FindMatches(content, query)` sans options garde l'ancien comportement :
sous-chaîne, sans tenir compte de la casse.

## Ouvrir et enregistrer un fichier

- **Depuis l'explorateur** (double-clic, `OpenFileIntent`) : l'OS lance l'éditeur
  avec `argv = [id de l'app, chemin]`. `WindowAppBase.LaunchFile` lit ce chemin
  (premier argument absolu) ; l'éditeur charge le fichier et garde son chemin, donc
  **Enregistrer** (Ctrl+S) écrit sur place, sans dialogue. Avant, le chemin
  n'arrivait jamais (`LaunchFilePath` n'était posé par personne) : l'éditeur
  s'ouvrait vide et Enregistrer se comportait comme Enregistrer sous. Le
  CodeEditor suit la même règle.
- **Texte ou binaire** se décide sur le contenu (`HackTextContent.TryDecode`,
  SDK) : UTF-8 strict sans octet de contrôle autre que tabulation, fins de ligne,
  saut de page, retour arrière et échappement. Un fichier vide créé par
  « New File » (étiqueté binaire par le système de fichiers) s'ouvre donc.
- **Enregistrer sous** vers un autre chemin n'exige plus la révision du fichier
  ouvert : la cible est créée, ou remplacée après confirmation dans le dialogue
  (avant : « Destination directory not found »).
- **Dialogues Ouvrir/Enregistrer** (Platform Blazor) : un double-clic ou Entrée
  sur un fichier l'ouvre (Ouvrir) ou reprend son nom (Enregistrer, qui demande
  ensuite avant de remplacer) ; le dialogue remplit sa fenêtre et la liste
  défile, les boutons du bas ne sont plus coupés.

## Arrêt demandé par l'OS (ADR 0069)

Quand l'OS demande à l'éditeur de s'arrêter (déconnexion, `kill`, « End
task ») alors que le document est modifié, `OnAppStopRequestedAsync` écrit le
texte dans un brouillon `HackRecoveryDraft`
(`~/.local/state/org.hackeros.text-editor/recovery/draft.json`, dossier
`0700`) avec le chemin du fichier d'origine ; l'OS attend cette écriture avant
de fermer la fenêtre. Au lancement suivant sans fichier, le brouillon est
repris (`TakeAsync`) comme un **nouveau** tampon modifié, titré
« nom (recovered) », et la barre d'état l'indique : il n'écrase jamais le
fichier d'origine sans que l'utilisateur choisisse où l'enregistrer. Fermer la
fenêtre soi-même passe toujours par la question « Save changes? » et ne laisse
pas de brouillon.

## Langues (ADR 0104)

Les menus, la barre de recherche, la barre d'état, les dialogues et les
messages d'erreur viennent de `Resources/Strings.resx` / `Strings.fr.resx`
(`AppContext.GetLocalizer<Strings>()`). `TextEditorDocument` reçoit le
localiseur de la fenêtre pour ses titres (« Sans titre », « (récupéré) ») et
`DescribeReplacementCount` le prend en paramètre ; sans localiseur, ils lisent
la langue du processus courant (anglais hors d'une app, donc dans les tests
unitaires). Les pluriels suivent la règle de la langue (0 et 1 au singulier en
français).

## Décisions

- Le remplacement ne gère pas les expressions régulières (choix par défaut de
  l'ADR 0058, retenu pour la phase 4).
- La sélection réelle de la zone de texte décide de « Replace », comme dans les
  éditeurs de bureau. Sans le module JavaScript (rendu sans navigateur), l'éditeur
  se rabat sur l'occurrence courante de la recherche.
- Le remplacement passe par `TextEditorDocument.Edit` : l'annulation native de la
  zone de texte (Ctrl+Z) ne couvre pas un remplacement, comme toute valeur écrite
  par Blazor.

## Liste des tâches (`rebuild/4-text-editor`)

- [x] Options « Match case » et « Whole word » dans `TextEditorFindEngine.FindMatches`.
- [x] `Replace`, `ReplaceAll`, `FindNextFrom` et `DescribeReplacementCount`, testés sans interface.
- [x] Ligne de remplacement dans la barre de recherche, Ctrl+H, menu « Edit → Replace ».
- [x] Lecture de la sélection de la zone de texte et blocage des raccourcis du navigateur.
- [x] Documentation, plan de refonte et index mis à jour.

## Liste des tâches (retours après `rebuild/4-text-editor`, livrés avec `rebuild/4-file-explorer`)

- [x] Lire le fichier de lancement depuis les arguments du processus (TextEditor, CodeEditor).
- [x] Décider texte/binaire sur le contenu (`HackTextContent`).
- [x] Enregistrer sous vers un nouveau fichier.
- [x] Dialogues Ouvrir/Enregistrer : double-clic, mise en page dans la fenêtre.
