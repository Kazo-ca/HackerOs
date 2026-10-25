# ADR 0166 : disposition mobile en fichier conf sous Desktop

## Statut

Accepté le 2026-10-23 à la demande de Benoit. Remplace le chemin et le format
JSON de l'[ADR 0165](0165-mobile-home-drawer-and-mouse-controls.md) ; les autres
décisions de cet ADR restent applicables.

## Décision

- La disposition est `~/Desktop/.mobile/layout.conf`, propre à chaque utilisateur.
- Le fichier est du texte selon la grammaire des paramètres de l'OS : commentaires
  `#`, clé `version=1`, section `[dock]` avec `apps=`, section `[pages]` avec des
  clés numériques contiguës de `1` à N. Chaque valeur est une liste ordonnée
  d'IDs séparés par des virgules ; une liste vide est permise. Ce format propre
  à HackerOS s'inspire des fichiers de configuration Linux.
- `ConfigDocumentFormat` lit la grammaire existante ; les limites et validations
  de l'accueil restent identiques. Les écritures normalisent le texte.
- `OsSettingsLocation.UserHome` ajoute une résolution relative au dossier personnel
  à `OsSettingsFile`, avec les accès et permissions utilisateur existants.
- Si la destination est absente, une ancienne disposition JSON valide est lue,
  puis enregistrée en conf. Le fichier source reste intact. Une destination
  existante, valide ou non, empêche la migration. Aucune nouvelle sauvegarde JSON.
- Les noms commençant par un point sont filtrés sur le bureau. L'explorateur
  permet toujours de les révéler avec sa commande des fichiers cachés.

## Vérification

Tests de format, limites, ordre, migration unique et priorité du fichier conf
dans `MobileHomeLayoutTests` ; persistance et visibilité dans `MobileHomeScreenTests`.
Format et exemple : [mobile-home-screen.md](../mobile-home-screen.md).
