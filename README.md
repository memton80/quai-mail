<p align="center">
  <img src="docs/images/logo.png" width="96" alt="Logo de Quai Mail">
</p>

<h1 align="center">Quai Mail</h1>

<p align="center">
  <b>Lire et écrire vos mails, simplement, dans une vraie application Windows.</b><br>
  Un client mail de base, gratuit, pensé pour Windows 11. De la même famille que <a href="https://github.com/memton80/quai-ssh">Quai</a> et <a href="https://github.com/memton80/quai-pdf">Quai PDF</a>.
</p>

<p align="center">
  <a href="https://github.com/memton80/quai-mail/releases/latest"><b>Télécharger Quai Mail pour Windows</b></a>
  · Gratuit · Sans compte · Sans publicité
</p>

> **Projet en cours de construction.** Version 0.6 : comptes IMAP / SMTP (réglages détectés, mot de passe dans le coffre de Windows), Gmail et Outlook.com par le navigateur (sans mot de passe), lecture sûre, écriture, recherche, corbeille, réception en direct avec notifications, boîte de réception unifiée, liens mail.

## Ce que sera Quai Mail

- Comptes IMAP / SMTP, plusieurs comptes, dossiers, recherche.
- Lire, répondre, transférer, pièces jointes, brouillons enregistrés en local.
- Application Windows native (WinUI 3). Le corps des mails est affiché par un WebView2 **verrouillé** : pas de JavaScript, images distantes bloquées par défaut, aucun lien suivi sans votre accord.
- **Aucune publicité, aucun pistage, aucun compte à créer.** Quai Mail ne parle qu'à vos serveurs mail et à GitHub, pour savoir s'il existe une nouvelle version (désactivable).
- Mots de passe gardés dans le coffre de Windows ; Gmail et Outlook.com par leur page de connexion officielle, sans que Quai Mail voie votre mot de passe.

## Toujours à jour, en un clic

Comme Quai : quand une nouvelle version sort, un bandeau la propose. Un clic sur **Mettre à jour** la télécharge, vérifie qu'elle est bien celle publiée, l'installe, puis Quai Mail redémarre tout seul au même endroit (il propose d'abord de garder votre brouillon en cours).

## Installer Quai Mail

1. Téléchargez **`QuaiMail-Setup-x64.exe`** depuis la [dernière version](https://github.com/memton80/quai-mail/releases/latest) (ou `QuaiMail-Setup-arm64.exe` pour un PC Snapdragon / ARM).
2. Lancez-le : pas besoin d'être administrateur, tout est inclus.

> Au premier lancement, Windows peut afficher « Windows a protégé votre ordinateur » : Quai Mail est un projet indépendant et ses fichiers ne sont pas encore signés. Cliquez sur **Informations complémentaires**, puis **Exécuter quand même**.

Fonctionne sur Windows 10 et Windows 11. Une version portable (zip, sans installation) est aussi proposée.

## En savoir plus

- [Le guide](docs/GUIDE.md) : la fenêtre, les réglages, les mises à jour, où sont vos données, les limites actuelles.
- [Pour les développeurs](docs/DEVELOPPEMENT.md) : organisation du code, tests, compilation, publication.
- [Consignes du projet](CLAUDE.md) · [Décisions techniques](docs/DECISIONS.md) · [Mission de départ](docs/PROMPT-CLAUDE-CODE.md)
- Une idée, un bug ? [Ouvrez un ticket](https://github.com/memton80/quai-mail/issues).

Quai Mail est gratuit et open source, sous licence MIT.
