> [English version](../../../../en/wasm/dev/adr/0201-switch-user-stays-greyed.md)

# ADR 0201 : « Changer d'utilisateur » reste grisé

## Statut

Accepté le 2026-10-30 par Benoit. Issue [#290](https://github.com/Kazo-ca/HackerOs/issues/290)
(étiquette `TODO`), série `todo/base`, chantier `todo/16-switch-user`.

- **Complète :** [ADR 0195](0195-start-menu-power-button.md) §4 (« Changer
  d'utilisateur » grisé dans le menu Marche/arrêt).

## Contexte

- Le menu Marche/arrêt du menu Démarrer (ADR 0195) affiche « Changer
  d'utilisateur » grisé : l'OS n'a qu'une session (`ISessionService.CurrentPrincipal`).
- La question « Comment Changer d'utilisateur doit-il fonctionner ? » a été posée
  à Benoit avec trois choix :
  1. sessions simultanées, comme Windows (plusieurs sessions ouvertes, une seule
     au premier plan) ;
  2. fermer la session puis choisir un compte (équivalent à Se déconnecter) ;
  3. garder l'entrée grisée.
- Un prototype du choix 1 a été écrit pendant l'attente de la réponse : services
  de session multiples, fenêtres cachées par session, écran de changement
  d'utilisateur, limite de 3 sessions, tests unitaires et de bout en bout. Il
  est gardé sur la branche `switch-user/simultaneous-sessions-prototype` et
  n'est pas fusionné.

## Décision

- Benoit a choisi le choix 3 : **« Changer d'utilisateur » reste grisé**, pour
  tout compte, invité compris. Le code de l'OS ne change pas.
- L'OS garde une seule session ouverte à la fois (ADR 0013). Pour passer à un
  autre compte : **Se déconnecter**, puis choisir le compte à l'écran de
  connexion.
- Le prototype n'est ni fusionné ni supprimé : il sert de point de départ si
  Benoit décide plus tard d'ouvrir plusieurs sessions. Ce jour-là, une nouvelle
  ADR remplacera celle-ci.

## Conséquences

- Aucune entrée de journal des nouveautés : rien ne change pour l'utilisateur.
- L'issue #290 n'a plus de travail à faire tant que la décision tient ; Benoit
  décide de la fermer ou de la garder pour plus tard.
- La branche du prototype n'est pas tenue à jour avec `main` ; la reprendre
  demandera de la fusionner de nouveau.
