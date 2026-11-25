<h1 align="center">Quai Mail</h1>

<p align="center">
  <b>Lire et écrire vos mails, simplement, dans une vraie application Windows.</b><br>
  Un client mail de base, gratuit, pensé pour Windows 11. De la même famille que <a href="https://github.com/memton80/quai-ssh">Quai</a> et <a href="https://github.com/memton80/quai-pdf">Quai PDF</a>.
</p>

> **Projet en cours de démarrage.** Première étape : la chaîne de mise à jour (même mécanisme que Quai et Quai PDF), puis les comptes IMAP / SMTP, la lecture et l'écriture.

## Ce que sera Quai Mail

- Comptes IMAP / SMTP, plusieurs comptes, dossiers, recherche.
- Lire, répondre, transférer, pièces jointes, brouillons enregistrés en local.
- Application Windows native (WinUI 3). Le corps des mails est affiché par un WebView2 **verrouillé** : pas de JavaScript, images distantes bloquées par défaut, aucun lien suivi sans votre accord.
- **Aucune publicité, aucun pistage, aucun compte à créer.** Quai Mail ne parle qu'à vos serveurs mail et à GitHub, pour savoir s'il existe une nouvelle version (désactivable).
- Mots de passe gardés dans le coffre de Windows.

## Toujours à jour, en un clic

Comme Quai : quand une nouvelle version sort, un bandeau la propose. Un clic sur **Mettre à jour** la télécharge, vérifie qu'elle est bien celle publiée, l'installe, puis Quai Mail redémarre tout seul au même endroit (il propose d'abord de garder votre brouillon en cours).

## Pour les développeurs

- [Consignes du projet](CLAUDE.md) · [Décisions techniques](docs/DECISIONS.md) · [Mission de départ](docs/PROMPT-CLAUDE-CODE.md)
- Une idée, un bug ? [Ouvrez un ticket](https://github.com/memton80/quai-mail/issues).

Quai Mail est gratuit et open source, sous licence MIT.
