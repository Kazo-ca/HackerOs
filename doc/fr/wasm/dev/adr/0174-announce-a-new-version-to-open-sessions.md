> [English version](../../../../en/wasm/dev/adr/0174-announce-a-new-version-to-open-sessions.md)

# ADR 0174 : annoncer une nouvelle version aux sessions ouvertes

## Statut

Accepté le 2026-10-26 à la demande de Benoit (« publier un fichier json qui
permet aux sessions de voir si une nouvelle version est disponible et de
l'afficher à l'utilisateur, par exemple toutes les 5 minutes ») ; source
choisie par Benoit : « Même site que l'OS ». Complète l'[ADR 0162](0162-restart-after-a-host-build-change.md)
et l'[ADR 0173](0173-automatic-releases-from-the-version-file.md).

## Décision

1. **`version.json`** : le build de `OS/HackerOs.Ecosystem` écrit
   `{ "product": "HackerOS", "version": "<HackerOsVersion>" }` dans `obj/` et le
   sert comme ressource web statique à la racine du site (`/version.json`). Il
   suit `build/Versions.props` : rien à modifier à la main, rien dans les
   sources.
2. **`HostReleaseMonitor`** (`HackerOs.Platform.Blazor`, à côté de
   `HostBuildUpdateMonitor`) lit ce fichier toutes les **5 minutes**
   (`DefaultInterval`) par `IHostReleaseProbe`. L'hôte navigateur
   (`BrowserHostReleaseProbe`) le demande avec `?t=<millisecondes>`, pour que
   le service worker n'y réponde pas avec sa copie en cache, et avec
   `cache: no-store`. Une version différente de `HackerOsSdk.Version` est
   annoncée une fois par version (`ReleaseAvailable`). Hors ligne, serveur
   arrêté ou fichier illisible : pas de mise à jour.
3. Chaque vérification demande aussi à `HostBuildUpdateMonitor` si le build a
   changé sous la page avec le même numéro (une publication de Pages sans
   montée de version) : le bandeau apparaît alors sans numéro.
4. **Affichage** : le bandeau existant de l'ADR 0162 dit « HackerOS 2.2.0 est
   disponible. Les applications ouvertes seront fermées. » avec le bouton
   « Redémarrer pour mettre à jour ». Le redémarrage recharge la page, qui
   prend la nouvelle version servie par le site.

## Conséquences

- Fonctionne pour tout site qui sert la PWA (GitHub Pages, `HackerOs.Server`,
  autre hôte statique) ; un hôte d'un autre dépôt sans `version.json` ne voit
  qu'un 404, donc aucune annonce.
- Une requête de quelques octets toutes les 5 minutes par session ouverte.

## Options écartées

- **Dernière Release GitHub** (`api.github.com`) : annoncerait une version que
  le site n'a peut-être pas encore, et dépendrait de github.com et de ses
  limites ; écartée par Benoit.
- **Ne compter que sur le service worker** : le navigateur ne revérifie le
  worker qu'à la navigation, et ne donne pas le numéro.
