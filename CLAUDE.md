# Quai Mail — consignes pour Claude Code

Lis ce fichier en entier avant de toucher au code. Le propriétaire (Alexandre) parle un français décontracté : réponds-lui en français, direct et pratique. Le code, les noms de classes et les commits restent en anglais ou en français selon ce qui est le plus clair, mais sois cohérent.

## Objectif

Client mail **de base** pour Windows, natif, interface WinUI 3 au look Windows 11. Même famille, même stack et mêmes règles que [Quai](https://github.com/memton80/quai-ssh) (client SSH) et [Quai PDF](https://github.com/memton80/quai-pdf), dont ce dépôt reprend l'organisation. Gratuit, MIT, sans compte, sans publicité, sans télémétrie.

## Priorité n°1 : les mises à jour

**Avant toute fonction de mail**, la chaîne de mise à jour doit marcher de bout en bout, **avec le même mécanisme que Quai et Quai PDF** (code repris, pas réécrit) :

- source GitHub `releases/latest` sans jeton (`memton80/quai-mail`), comparaison SemVer, bandeau « nouvelle version » en haut de la fenêtre ;
- un clic sur **Mettre à jour** : téléchargement de `QuaiMail-Setup-<arch>.exe`, vérification SHA-256 avec le `digest` publié par GitHub, installation Inno Setup silencieuse (`/SILENT /RELAUNCH=1`), fermeture, relance par `--after-update` ;
- copie portable (zip) : le bouton ouvre la page de la release ;
- réglage `checkForUpdates` pour couper toute connexion à GitHub ;
- avant d'installer, si un **brouillon** est en cours : proposer de le garder (il est enregistré en local), « Annuler » laisse la mise à jour en attente, déjà téléchargée ;
- la nouvelle version restaure l'état de la fenêtre (compte, dossier, mail ouvert, brouillon) puis efface `updates/reopen.txt` ;
- le CI le prouve à chaque run (`--update-test`, voir plus bas). Une mise à jour cassée est un bug bloquant : on ne livre aucune autre fonction tant qu'elle ne passe pas.

Déjà en place dans ce dépôt : `src/QuaiMail.Core/Updates/`, `UpdateBannerViewModel`, `AppPaths`, `SettingsService`, leurs tests, l'installateur `installer/QuaiMail.iss`, le workflow `build.yml`. Reste à **porter la partie App** depuis `memton80/quai-pdf` (voir `docs/PROMPT-CLAUDE-CODE.md`).

## Règle n°1 : ne pas réinventer la roue

Avant d'écrire une brique toi-même, cherche une bibliothèque ou un composant existant et maintenu (NuGet, GitHub, API de Windows). Si tu écris quand même ta propre version, note la raison dans `docs/DECISIONS.md` (une ligne par décision). **Toute dépendance ajoutée** : version vérifiée sur nuget.org, licence compatible MIT (pas de GPL / AGPL / SSPL), une ligne dans `DECISIONS.md`, ajoutée dans `Directory.Packages.props` (gestion centralisée).

| Besoin | Brique |
|---|---|
| IMAP / SMTP | `MailKit` (MIT) |
| Format des mails (MIME) | `MimeKit` (MIT, tiré par MailKit) |
| Cache local, recherche | SQLite (`Microsoft.Data.Sqlite`), recherche plein texte FTS5 |
| Nettoyage du HTML des mails | une bibliothèque de sanitisation maintenue (ex. `HtmlSanitizer`, MIT) — à vérifier avant usage |
| Affichage du corps des mails | **WebView2 verrouillé** (voir plus bas), seule exception à « pas de page web » |
| Mots de passe, jetons | coffre de Windows (`Windows.Security.Credentials.PasswordVault`), jamais dans un fichier |
| OAuth2 Gmail / Outlook (plus tard) | MSAL / `Google.Apis.Auth`, ou navigateur système + PKCE |
| MVVM | `CommunityToolkit.Mvvm` |
| DI, logs | `Microsoft.Extensions.*` + `Serilog` (fichier) |
| Réglages | `System.Text.Json` (source generator), dossier `%LOCALAPPDATA%\QuaiMail` |
| Sélecteurs de fichiers | `Microsoft.Windows.Storage.Pickers` (Windows App SDK) |
| Mises à jour | code de Quai (`Updates/`), `Semver`, releases GitHub de `memton80/quai-mail` |
| Tests | `xUnit` (`xunit.v3`) |

## Stack et cible

- C#, WinUI 3 / Windows App SDK 2.6.5 (comme Quai), application **non packagée** (`WindowsPackageType=None`), self-contained.
- .NET 10 (LTS). Architectures `x64` et `arm64`. Windows 10 (1809+) et 11.
- Interface en français d'abord (ressources `.resw`, extension `{l:Str Key=…}`).

## Structure

```
QuaiMail.sln
src/QuaiMail.Core/          logique sans aucune dépendance UI (mail, cache, réglages, mises à jour, view-models)
src/QuaiMail.App/           application WinUI 3 (vues, WebView2 de lecture, dialogues, test de fumée)
src/QuaiMail.Agent/         agent de notification QuaiMailAgent.exe (Native AOT, zone de notification ; 10 Mo de mémoire au plus, vérifié par le CI)
tests/QuaiMail.Core.Tests/  tests xUnit sur QuaiMail.Core (tournent aussi sous Linux)
installer/QuaiMail.iss      installateur Inno Setup
docs/DECISIONS.md           journal des choix techniques
docs/PROMPT-CLAUDE-CODE.md  mission de départ
```

`QuaiMail.Core` ne référence jamais WinUI. Tout ce qui est testable va dans `QuaiMail.Core`.

## Principes du mail

- **Aucun mail n'est jamais envoyé sans geste explicite** de l'utilisateur (bouton Envoyer). Pas d'envoi automatique, pas de réponse automatique, pas de « test » qui envoie un vrai message.
- **Suppression** : vers la corbeille du serveur par défaut ; suppression définitive seulement après confirmation.
- Brouillons enregistrés **en local** régulièrement (une mise à jour ou un plantage ne les perd jamais), puis synchronisés dans le dossier Brouillons.
- Le serveur reste la référence : le cache SQLite peut être effacé et reconstruit sans perte.
- **Secrets et contenu** : ni mot de passe, ni jeton, ni objet, ni corps, ni adresse de correspondant dans les journaux. Les erreurs réseau journalisent le serveur et le code, pas le contenu.
- Certificats TLS : toujours vérifiés. Un certificat invalide = erreur claire ; jamais de « ignorer » silencieux (si un jour une exception par empreinte est ajoutée, elle est explicite et par compte).
- Connexions autorisées : les serveurs mail configurés par l'utilisateur, GitHub pour les mises à jour (désactivable), et keys.openpgp.org pour la clé inconnue d'un mail signé (empreinte seulement, désactivable ; décision d'Alexandre). Aucune télémétrie.
- Pièces jointes : jamais ouvertes automatiquement ; avertissement sur les types exécutables ; enregistrement atomique (`AtomicFile`).

## WebView2 : verrouillé (exception documentée)

Le corps des mails est du HTML : WebView2 est la seule option réaliste dans WinUI 3. Il ne sert **qu'à lire un mail**, jamais à charger une page web.

- JavaScript désactivé (`IsScriptEnabled = false`), pas de messages web, menus contextuels par défaut et outils de développement désactivés, pas de zoom par pincement / raccourcis navigateur, pas de remplissage automatique, pas de téléchargements.
- HTML du mail **nettoyé** avant affichage (scripts, `iframe`, `object`, `embed`, formulaires, gestionnaires `on…`), et mis dans un gabarit local dont la politique `Content-Security-Policy` interdit tout chargement distant.
- **Images distantes bloquées par défaut** (réglage `blockRemoteImages`, vrai par défaut) avec un bouton « Afficher les images » par mail et, plus tard, par expéditeur. Les images jointes (`cid:`) s'affichent.
- **Navigation interdite** : tout changement de page est annulé ; un clic sur un lien montre l'adresse réelle et l'ouvre dans le navigateur par défaut après confirmation, seulement pour `http`, `https` et `mailto`.
- Dossier de données WebView2 dans `%LOCALAPPDATA%\QuaiMail\webview`, vidé à la fermeture ; aucun cookie persistant.
- Runtime WebView2 : « Evergreen » de Microsoft, déjà présent sur Windows 11 et la plupart des Windows 10. S'il manque : message clair avec le lien officiel (rien n'est embarqué ni téléchargé en douce).
- Le CI vérifie que la publication ne contient **aucun JavaScript** et aucun HTML hors `Assets/Mail/`.

## Règle CSS / HTML

Le CSS ne va **jamais** dans le HTML : fichiers séparés, pas d'attribut `style=""`, pas de balise `<style>`. Ici : le gabarit de lecture est `Assets/Mail/reader.html` (aucun style) et ses styles sont dans `Assets/Mail/mail.css` (thème clair / sombre, typographie, citations), servis à la WebView2 par un nom d'hôte virtuel (`SetVirtualHostNameToFolderMapping`). Le CI le contrôle. Les styles propres au contenu d'un mail reçu (qui n'est pas notre code) ne sont pas concernés, mais passent par le nettoyage. XAML n'est pas concerné, mais les styles vont dans `Styles/Common.xaml`, pas en ligne dans les vues.

## UI

Fluent / Windows 11 : barre de commandes en boutons arrondis, Mica, coins arrondis, thèmes clair / sombre / système, navigation au clavier, textes d'accessibilité sur les boutons icônes. Trois volets : comptes et dossiers, liste des mails, lecture. Reste sobre.

## CI et releases (GitHub Actions uniquement)

- `.github/workflows/build.yml` sur `windows-latest` : tests, publication x64 / arm64, vérification du contenu, zip, installateur, installation silencieuse, **test de fumée** sur l'application installée (`QuaiMail.exe --smoke-test <dossier>`, captures en artefact), **mise à jour en place** (`--update-test` : téléchargement vérifié, installation silencieuse, relance, état restauré), désinstallation complète.
- Après chaque push, vérifie le résultat du run (`gh run watch`, `gh run view --log-failed`) et corrige jusqu'à ce que ce soit vert. Ne déclare rien terminé tant que le CI n'est pas vert.
- Release : automatique, comme Quai. Monter `<Version>` dans `Directory.Build.props` ; quand ce numéro arrive sur `main` sans tag, le CI crée le tag `vX.Y.Z` et publie la release (installateurs x64 / arm64 + zips, empreintes SHA-256).
- Le test de fumée n'utilise **jamais** un vrai compte mail : un faux serveur local ou des données de test, rien d'autre.

## Façon de travailler

- Travaille en autonomie : décide, avance, note les choix dans `docs/DECISIONS.md`. Ne pose une question à Alexandre que pour un **choix structurant non prévu ici** (changer de lib majeure, passer en MSIX, dépendance sous licence copyleft, changer de licence) ou une action risquée / irréversible : préviens avant.
- Petits commits, messages clairs, travail sur des branches de développement ; seule `main` publie une release.
- Un jalon = une release (`Directory.Build.props` monté). Le détail des jalons est dans `docs/PROMPT-CLAUDE-CODE.md`.
- Si une action est risquée ou irréversible, préviens avant.
