# Mission de départ pour Claude Code

Colle ce texte dans Claude Code, à la racine du dépôt `quai-mail`.

````text
Tu travailles dans le dépôt quai-mail (client mail Windows, WinUI 3, famille Quai / Quai PDF). Lis d'abord CLAUDE.md et docs/DECISIONS.md en entier : ce sont les règles. Clone aussi memton80/quai-pdf (public) à côté, comme référence : tu y portes le code de la partie App (le Core générique des mises à jour est déjà dans ce dépôt).

Travaille en AUTONOMIE, jalon après jalon, sans me demander la permission d'avancer. Tu ne me poses une question que pour un choix structurant non prévu dans CLAUDE.md (lib majeure, MSIX, licence copyleft, changement de licence) ou une action risquée / irréversible : tu me préviens avant. Pour tout le reste, tu décides, tu notes le choix en une ligne dans docs/DECISIONS.md, et tu continues. Si une question me bloque sur un point, avance sur un autre. Boucle de travail : coder, pousser, vérifier le CI avec gh (gh run watch, gh run view --log-failed), corriger jusqu'au vert. Aucun jalon n'est terminé tant que le CI n'est pas vert et que la release du jalon n'est pas publiée. À la fin de chaque jalon, un court message en français : ce qui marche, la version publiée, le suivant.

Prérequis : le dépôt GitHub memton80/quai-mail doit exister (public). S'il n'existe pas, préviens-moi avant de le créer. Le secret VT_API_KEY (VirusTotal) est facultatif.

JALON 0 : LES MISES À JOUR (PRIORITÉ ABSOLUE, release 0.1.0)
Objectif : une coquille Quai Mail qui s'installe, se lance et se met à jour toute seule, exactement comme Quai PDF. Aucune fonction de mail avant que ce jalon soit vert.
1. Porte src/QuaiPdf.App vers src/QuaiMail.App (renomme tout : QuaiPdf devient QuaiMail, Quai PDF devient Quai Mail, quai-pdf devient quai-mail) : App.xaml(.cs) avec les arguments --smoke-test, --update-test et --after-update, MainWindow avec le bandeau de mise à jour, Services (UpdateInstaller, DialogService, ThemeService, ScreenCapture, ReswStrings, AppServices, SmokeUpdate), Localization (Loc, StrExtension), Strings/fr-FR/Resources.resw, Styles/Common.xaml, SettingsDialog, NativeMethods, UiHelpers, app.manifest, SmokeTest. Retire tout ce qui est PDF (rendu, texte, vignettes, document, métadonnées, « Ouvrir avec »).
2. QuaiMail.App.csproj : repars de celui de Quai PDF mais SUPPRIME les réglages et cibles qui excluent WebView2 (WebView2EnableCsWinRTProjection*, QuaiMailExcludeWebView2Loader) : WebView2 sert à lire les mails (voir CLAUDE.md, « WebView2 : verrouillé »). Ajoute QuaiMail.App au .sln. Remets la cible de test dans la CI (déjà écrite dans .github/workflows/build.yml).
3. Interface minimale : fenêtre Mica, trois volets vides (comptes et dossiers, liste, lecture), bandeau de mise à jour, réglages (thème, vérifier les mises à jour, images distantes bloquées), version affichée. Remplace SaveChoice / AskSaveDraftAsync par la vraie boîte « Garder ce brouillon ? » (pas encore de brouillon : le chemin existe, le test de fumée le simule). Crée Assets/Mail/reader.html (aucun style, aucun script) et Assets/Mail/mail.css (clair / sombre) même s'ils ne servent qu'au jalon 2, car le CI les exige. Icône propre à Quai Mail (adapte tools/make_icon.py : Assets/QuaiMail.ico et AppIcon.png).
4. « État de la fenêtre » : avant une mise à jour, écris l'état (compte, dossier, mail ouvert, brouillon éventuel) dans updates/reopen.txt ; la nouvelle version, lancée avec --after-update, le restaure une fois puis efface le fichier, en journalisant exactement « Démarrage juste après une mise à jour » puis « État de la fenêtre restauré après la mise à jour » (le CI cherche ces deux lignes). Le --update-test simule un brouillon ouvert et un état de fenêtre.
5. Tests xUnit sur tout ce qui est dans Core (mise à jour, réglages, état de la fenêtre). Vérifie que le test de ressources (clés .resw) est porté aussi.
6. docs/GUIDE.md (utilisateur) et docs/DEVELOPPEMENT.md (code, tests, build, publication), sur le modèle de Quai PDF.
7. Pousse. CI entièrement vert : tests, publication x64 et arm64, vérification du contenu (WebView2 présent, aucun JS, aucun HTML hors Assets/Mail, aucun CSS dans le HTML), installation silencieuse, test de fumée, mise à jour en place, désinstallation complète. La release v0.1.0 est créée par le CI avec QuaiMail-Setup-x64.exe, QuaiMail-Setup-arm64.exe et les zips.
8. Preuve de la chaîne réelle : vérifie avec gh api que les assets de la release ont bien un champ digest (sha256:…), car la mise à jour le vérifie. Puis monte la version en 0.1.1 sur une branche, fusionne dans main, et vérifie que le CI publie 0.1.1 et que la logique de détection (UpdateChecker) voit 0.1.1 comme plus récent que 0.1.0 (test automatisé avec la vraie réponse de l'API, sans jeton).

JALON 1 : COMPTES ET DOSSIERS (0.2.0)
MailKit (IMAP, SMTP). Ajout d'un compte IMAP/SMTP (adresse, serveur, port, sécurité TLS, mot de passe ou mot de passe d'application), détection automatique des réglages quand c'est possible (autoconfiguration publiée par le fournisseur, sinon saisie manuelle), mot de passe dans le PasswordVault de Windows, plusieurs comptes. Liste des dossiers, liste des mails (en-têtes seulement, chargés par pages), cache SQLite (Microsoft.Data.Sqlite) dans %LOCALAPPDATA%\QuaiMail qu'on peut effacer sans perte. Tests de Core avec un faux serveur local (jamais de vrai compte).

JALON 2 : LIRE (0.3.0)
Lecture d'un mail dans la WebView2 verrouillée, exactement selon CLAUDE.md (JavaScript coupé, HTML nettoyé, CSP, navigation interdite, liens ouverts dans le navigateur après confirmation, images distantes bloquées avec bouton « Afficher les images »). Texte brut en repli. Pièces jointes : liste, enregistrement atomique, jamais d'ouverture automatique. Marquer lu / non lu, étoile. Tests de nettoyage du HTML avec des mails piégés (script, iframe, formulaire, pixel espion, liens trompeurs).

JALON 3 : ÉCRIRE (0.4.0)
Nouveau message, répondre, répondre à tous, transférer, pièces jointes, brouillons enregistrés en local toutes les quelques secondes puis synchronisés, envoi SMTP uniquement sur geste explicite, copie dans Envoyés. La mise à jour garde le brouillon (jalon 0, chemin déjà prêt).

JALON 4 : CONFORT (0.5.0)
Recherche plein texte (SQLite FTS5), déplacer / supprimer (corbeille d'abord, définitif après confirmation), IDLE (réception en direct), reconnexion automatique, notifications Windows, boîte de réception unifiée, « Ouvrir avec » pour mailto:.

JALON 5 : OAUTH2 (0.6.0)
Gmail et Outlook par OAuth2 (navigateur système, PKCE), sans jamais stocker de mot de passe. Demande-moi d'abord pour l'inscription de l'application chez Google et Microsoft : ce sont des comptes à moi, tu prépares tout ce qu'il faut et tu me dis ce que je dois cliquer.

À chaque jalon : monter <Version> dans Directory.Build.props, mettre à jour docs/GUIDE.md et docs/DECISIONS.md, CI vert, release publiée. Ne touche jamais à main en forçant (pas de push --force), n'envoie jamais de vrai mail pendant les tests, ne mets aucun secret dans le dépôt.
````
