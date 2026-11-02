> [English version](../../../en/wasm/dev/sign-in-factory-reset.md)

# Mot de passe oublié : réinitialiser depuis la connexion

## Objet

Quand plus personne ne connaît de mot de passe administrateur, l'écran de
connexion propose d'effacer HackerOS dans ce navigateur et de recommencer à
la première configuration. Décision :
[ADR 0226](adr/0226-forgot-password-factory-reset.md).

## Pour l'utilisateur

1. Sur l'écran de connexion, sous **Ouvrir la session**, cliquer sur
   **Mot de passe oublié ?**.
2. La boîte « Réinitialiser cette machine ? » liste ce qui sera supprimé :
   tous les comptes (mot de passe et clés d'accès), tous les fichiers, les
   apps installées et leurs données, tous les réglages, thèmes et journaux.
3. Taper `EFFACER` (`ERASE` en anglais) puis **Effacer et redémarrer**.
   **Annuler** ou Échap referme la boîte sans rien effacer.
4. L'écran de démarrage s'affiche, la page se recharge et HackerOS repart sur
   la création du premier compte (ou **Démarrer en invité**).

Le lien n'apparaît pas tant que l'invité est le seul compte : il n'a pas de
mot de passe à oublier.

## Pour celui qui déploie

Le lien est activé par défaut. Pour le retirer (poste partagé, démonstration
publique), mettre la clé à `false` dans `wwwroot/appsettings.json` des
fichiers publiés, sans recompiler :

```json
{
  "HackerOs": {
    "SignIn": {
      "FactoryReset": { "Enabled": false }
    }
  }
}
```

Le serveur HackerOS (`HackerOs.Server`) sert les fichiers publiés de l'OS tels
quels : la même clé s'applique. Une valeur absente ou illisible vaut `true`.
Un administrateur peut toujours effacer la machine depuis Paramètres ▸
Système.

## Architecture

| Élément | Rôle |
| --- | --- |
| `SignInFactoryResetSettings` (`Ecosystem.Host`) | Lit `HackerOs:SignIn:FactoryReset:Enabled` une fois au démarrage ; enregistré par `AddHackerOsBrowserHost`. |
| `SignInFactoryReset` (`Ecosystem.Host`) | `IsOffered` (clé de déploiement, `true` si l'hôte n'a rien enregistré) et `RunAsync`, qui revérifie la clé puis appelle le service d'alimentation. |
| `SignInFactoryResetPrompt` (`.razor`, `.razor.cs`, `.razor.css`) | Lien et boîte de confirmation ; `IsConfirmationTyped` compare le mot tapé sans tenir compte de la casse ni des espaces autour. |
| `SystemPowerService.FactoryResetFromSignInAsync` (`Platform.Core`) | Même séquence que `FactoryResetAsync` (journaux vidés, session fermée, base IndexedDB supprimée, page rechargée) ; audit `system.factory-reset` avec `origin = sign-in`. |
| `App.razor` | Affiche le lien sous le formulaire de mot de passe ; à la confirmation, montre l'écran de démarrage et lance `SignInFactoryReset.RunAsync`. |

## Limites

- Seule la base de l'OS est supprimée, comme pour la réinitialisation de
  Paramètres : les bases des sites web (`hackweb.*`) et les fichiers des apps
  installées dans le Cache Storage restent dans le navigateur, inutilisés.
- Les clés d'accès enregistrées restent dans l'authentificateur de
  l'appareil ; l'OS ne les connaît plus.
- Un autre onglet ouvert sur l'OS perd sa base et doit être rechargé.

## Tests

- `Tests/HackerOs.Ecosystem.Tests/SignInFactoryResetTests.cs` : lecture de la
  clé, `appsettings.json` publié, mot de confirmation, déploiement désactivé
  qui n'efface rien, rendu fermé et ouvert en anglais et en français.
- `Tests/HackerOs.Platform.Core.Tests/Administration/SystemAdministrationGatewayTests.cs` :
  effacement puis redémarrage, entrée d'audit `origin = sign-in`.

## Liste des tâches

- [x] ADR 0226 et questions posées à Benoit.
- [x] Clé de déploiement `HackerOs:SignIn:FactoryReset:Enabled`.
- [x] `SystemPowerService.FactoryResetFromSignInAsync` et son audit.
- [x] Lien et boîte de confirmation sur l'écran de connexion (fr, en).
- [x] Tests unitaires et de rendu.
- [x] Documentation, `features.md`, entrée de changement.
