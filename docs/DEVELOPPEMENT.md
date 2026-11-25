# Développement

## Organisation

| Dossier | Contenu |
|---|---|
| `src/QuaiMail.Core` | sans interface : comptes et détection des réglages (`Accounts/`), connexion OAuth2 par le navigateur (`Accounts/OAuth/` : PKCE, retour local, jetons), lecture (`Reading/` : nettoyage du HTML, gabarit, pièces jointes), rédaction (`Compose/` : réponses, destinataires, MIME ; `ViewModels/ComposerViewModel.cs`), IMAP / SMTP par MailKit (`Mail/MailKitMailService.cs`), réception en direct (`Mail/MailWatcher.cs`), cache SQLite et recherche FTS5 (`Cache/`), signature OpenPGP (`Pgp/PgpKeyStore.cs`, `ViewModels/PgpKeysViewModel.cs`), mises à jour (`Updates/`), réglages et fichiers (`Storage/`), brouillons locaux (`Drafts/`), état de la fenêtre (`Models/WindowState.cs`, `Storage/WindowStateStore.cs`), view-models (`ViewModels/`) |
| `src/QuaiMail.App` | WinUI 3 : fenêtre et ses trois volets, ajout de compte (`Views/AccountDialog.xaml`), lecture dans la WebView2 verrouillée (`Views/ReaderView.xaml.cs`), rédaction (`Views/ComposerView.xaml`), coffre de Windows (`Services/PasswordVaultCredentialStore.cs`), identifiants OAuth2 intégrés et fournisseur simulé du test de fumée (`Services/BrowserSignIn.cs`), bandeau de mise à jour, dialogues, réglages, installation des mises à jour (`Services/UpdateInstaller.cs`), test de fumée (`SmokeTest.cs`, `Services/SmokeMailService.cs`), gabarit de lecture des mails (`Assets/Mail`) |
| `tests/QuaiMail.Core.Tests` | xUnit : nettoyage du HTML avec des mails piégés (`Reading/HtmlCleanerTests.cs`), comptes et détection, service MailKit contre de faux serveurs IMAP / SMTP locaux en TLS (`Support/FakeImapServer.cs`, `FakeSmtpServer.cs`), cache SQLite, view-models, mises à jour, réglages, brouillons, état de la fenêtre, clés de traduction |
| `installer/QuaiMail.iss` | installateur Inno Setup (par utilisateur, sans droits administrateur) |
| `tools/make_icon.py` | génère l'icône (Pillow) |

`QuaiMail.Core` ne dépend pas de Windows : ses tests tournent aussi sous Linux.

## Commandes

```
dotnet test --project tests/QuaiMail.Core.Tests/QuaiMail.Core.Tests.csproj
dotnet build src/QuaiMail.App/QuaiMail.App.csproj -p:Platform=x64        (Windows seulement)
dotnet publish src/QuaiMail.App/QuaiMail.App.csproj -c Release -r win-x64 -p:Platform=x64 -o publish/x64
publish\x64\QuaiMail.exe --smoke-test %TEMP%\quaimail-smoke
python tools/make_icon.py src/QuaiMail.App/Assets/QuaiMail.ico src/QuaiMail.App/Assets/AppIcon.png docs/images/logo.png
```

La variable d'environnement `QUAI_MAIL_DATA_DIR` redirige toutes les données (réglages, brouillons, journaux) : les tests de fumée s'en servent pour ne jamais toucher au vrai profil.

## Arguments de la ligne de commande

| Argument | Rôle |
|---|---|
| `--smoke-test <dossier>` | test de fumée du CI : réglages, thème sombre, brouillon simulé et « Garder ce brouillon ? », état de la fenêtre noté puis restauré, comptes, lecture, écriture, confort, connexion par le navigateur (fournisseur simulé), bandeau de mise à jour (release fictive, sans réseau) ; captures et rapport dans le dossier, code 0 ou 1 |
| `--update-test <dossier> <installateur>` | mise à jour en place du CI : l'installateur local est présenté comme une release plus récente (vraie taille, vraie empreinte), un brouillon et un état de fenêtre sont simulés ; Quai Mail télécharge, vérifie, garde le brouillon, lance l'installateur et se ferme |
| `--after-update` | passé par l'installateur quand il relance Quai Mail : restaure l'état noté dans `updates/reopen.txt`, une fois |
| `--unregister-notifications` | passé par le désinstallateur : retire Quai Mail des notifications de Windows, sans fenêtre |
| `mailto:…` | lien mail (Quai Mail choisi pour les liens mail) : nouveau message prérempli ; si Quai Mail est déjà ouvert, le lien lui est passé (instance unique, `Program.cs`) |
| `--purge-secrets` | passé par le désinstallateur (effacement des données) : retire du coffre de Windows tous les mots de passe de Quai Mail, sans fenêtre |

## Signature OpenPGP

`PgpKeyStore` (MimeKit.Cryptography, sur BouncyCastle) : trousseau dans `pgp/` (format de MimeKit, `pubring.gpg` / `secring.gpg`), clé de signature choisie selon ses drapeaux, sa date d'expiration et sa révocation, signature PGP/MIME (SHA-256) du corps au moment d'envoyer (`ComposerViewModel.SendAsync`) ; la copie dans Brouillons n'est jamais signée. Les tests créent leurs clés pendant le test (aucune clé privée dans le dépôt) et vérifient la signature avec la clé publique seule ; si `gpg` est installé, un test crée une clé Ed25519 avec GnuPG, la fait signer par Quai Mail et vérifie avec `gpg --verify`. Le test de fumée importe une clé, envoie un mail signé (phrase refusée puis acceptée) et vérifie sa signature.

## Connexion par le navigateur (OAuth2)

**À venir** : `BrowserSignIn.Available` vaut `false`, les boutons sont grisés « à venir » et le test de fumée vérifie qu'ils le sont. Le passer à `true` rebranche tout (le test de fumée rejoue alors une connexion Google complète contre un fournisseur simulé).

Inscription chez Google et Microsoft : `docs/OAUTH.md`. Pour une compilation locale avec les boutons, définir avant `dotnet publish` les variables d'environnement `QuaiMailGoogleClientId`, `QuaiMailGoogleClientSecret` et `QuaiMailMicrosoftClientId` (sinon les boutons sont masqués). Les tests de `QuaiMail.Core` simulent le navigateur et le point d'émission des jetons, et les faux serveurs IMAP / SMTP acceptent `AUTH=XOAUTH2` : aucun vrai compte Google ou Microsoft n'est utilisé.

## Tests sans vrai compte

Aucun test n'utilise de vrai compte ni n'envoie de mail. Les tests de `QuaiMail.Core` démarrent de faux serveurs IMAP et SMTP sur `127.0.0.1` (port libre), en TLS ou STARTTLS avec un certificat auto-signé créé pour le test ; le service ne l'accepte que parce que le test l'épingle (sa validation normale le refuse, ce qui est testé aussi). Le faux SMTP refuse tout envoi et le compte ; pour tester l'envoi, son option `AcceptMail` lui fait recevoir le mail en mémoire, sans jamais l'acheminer. Seul `ComposerViewModel.SendCommand` (le bouton Envoyer) appelle `IMailService.SendAsync`. Le test de fumée de l'application utilise un serveur en mémoire (`SmokeMailService`) et une ressource du coffre de Windows réservée aux tests.

## Chaîne de mise à jour

1. `UpdateBannerViewModel.CheckAsync` → `UpdateChecker` → `GitHubReleaseSource` (`releases/latest`, sans jeton) : version SemVer plus récente, installateur de l'architecture (`QuaiMail-Setup-x64.exe` / `-arm64.exe`) avec son `digest` SHA-256.
2. Clic sur **Mettre à jour** : `UpdateDownloader` télécharge dans `updates/` et vérifie taille et SHA-256.
3. `UpdateInstaller.InstallAndRestartAsync` → `ShellViewModel.PrepareForUpdateAsync` : « Garder ce brouillon ? », puis `WindowStateStore.SaveForUpdate` écrit `updates/reopen.txt` ; l'installateur est lancé avec `/SILENT /RELAUNCH=1`, Quai Mail se ferme.
4. L'installateur relance `QuaiMail.exe --after-update` → `ShellViewModel.RestoreAfterUpdate` lit puis efface `reopen.txt`, rouvre le brouillon.

Le CI rejoue toute la chaîne à chaque run (étape « Mise à jour en place ») et cherche dans le journal de la version relancée : « Démarrage juste après une mise à jour », « Brouillon rouvert après la mise à jour », « État de la fenêtre restauré après la mise à jour ».

## Ajouter un texte à l'interface

Toutes les chaînes sont dans `src/QuaiMail.App/Strings/fr-FR/Resources.resw`, utilisées par `{l:Str Key=…}` dans le XAML, `Loc.Get` dans l'application et `IStrings` dans les view-models. Un test vérifie que chaque clé utilisée existe.

## WebView2 de lecture

La seule page web de Quai Mail est le corps d'un mail. Chaîne : `MessageContent.BodyHtml` (HtmlSanitizer) → `ReaderPage.Build` (gabarit `reader.html`, politique CSP) → `ReaderView` sert la page depuis la mémoire à `https://message.quaimail.example/page/<n>` et refuse toute autre adresse (sauf la feuille de style, hôte virtuel `reader.quaimail.example`). Toute modification de ces règles se fait avec un test de mail piégé à l'appui ; le test de fumée vérifie les réglages de la WebView2 et l'absence de requête vers l'extérieur.

## Styles

XAML : tous les styles dans `src/QuaiMail.App/Styles/Common.xaml`, pas de mise en forme en ligne répétée. Lecture des mails : le gabarit `Assets/Mail/reader.html` ne contient ni style ni script, ses styles sont dans `Assets/Mail/mail.css`. Le CI refuse tout HTML ou JavaScript hors de `Assets/Mail`, et toute balise `<style>` ou attribut `style=` dans le gabarit.

## Publier une version

Monter `<Version>` dans `Directory.Build.props` et pousser sur `main` : le CI construit, teste, installe, lance le test de fumée et la mise à jour en place, désinstalle, puis crée le tag `vX.Y.Z` et publie la release (installateurs et zips x64 / arm64, empreintes SHA-256). Une branche de développement construit et teste sans publier.
