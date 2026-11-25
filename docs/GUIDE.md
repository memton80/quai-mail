# Guide de Quai Mail

> **Version 0.4 : écrire.** Ajoutez vos comptes IMAP / SMTP, lisez vos mails en toute sécurité, écrivez, répondez, transférez. Recherche, déplacement et réception en direct arrivent dans la version 0.5.

## La fenêtre

Trois volets, de gauche à droite :

| Volet | Contenu |
|---|---|
| **Comptes et dossiers** | en haut, **Nouveau message** ; vos comptes mail et leurs dossiers (Boîte de réception, Envoyés…), avec le nombre de mails non lus ; en bas, **Ajouter un compte** et **Réglages** |
| **Mails** | les mails du dossier choisi, du plus récent au plus ancien : expéditeur, objet, date ; les non lus en gras |
| **Lecture** | le mail ouvert : expéditeur, destinataires, date, pièces jointes, puis son contenu ; ou le message que vous écrivez |

Tout se fait aussi au clavier : `Tab` passe d'un volet à l'autre, les flèches parcourent les dossiers et les mails, `F5` actualise les dossiers, `Ctrl+N` écrit un nouveau message.

## Ajouter un compte

1. **Ajouter un compte** (en bas à gauche).
2. Saisissez votre **adresse mail** : Quai Mail cherche aussitôt les réglages du serveur :
   - pour les grands fournisseurs (Gmail, Yahoo, iCloud, Orange, Free, SFR, La Poste…), il les connaît déjà, sans rien demander à personne ;
   - sinon, il lit ceux que votre fournisseur publie lui-même sur son site (autoconfiguration) ;
   - à défaut, il propose les réglages habituels (`imap.` et `smtp.` suivis de votre domaine), à corriger si besoin dans **Réglages du serveur**.
3. Saisissez votre **mot de passe**. Gmail, Yahoo, iCloud et quelques autres demandent un **mot de passe d'application** : un mot de passe à part, créé dans les réglages de sécurité de votre compte chez eux. Quai Mail vous le signale.
4. **Ajouter** : Quai Mail se connecte pour vérifier (réception IMAP et envoi SMTP, **sans rien envoyer**). En cas d'échec, il dit pourquoi (serveur introuvable, mot de passe refusé, certificat non fiable…) et rien n'est enregistré.

Le mot de passe est gardé dans le **coffre de Windows** (Gestionnaire d'identification), jamais dans un fichier. Les connexions sont toujours chiffrées (SSL / TLS ou STARTTLS) et le certificat du serveur toujours vérifié : un certificat non fiable est refusé, sans possibilité de l'ignorer.

Outlook.com et Hotmail n'acceptent plus les mots de passe : leur connexion par le navigateur arrive dans une prochaine version.

**Retirer un compte** : clic droit sur le compte (ou l'un de ses dossiers), **Retirer ce compte de Quai Mail…**, puis confirmer. Ses réglages, son mot de passe et son cache disparaissent de cet ordinateur ; vos mails restent sur le serveur.

## Dossiers et mails

- Les dossiers particuliers (Boîte de réception, Brouillons, Envoyés, Archives, Indésirables, Corbeille) viennent en premier, puis les autres, sous-dossiers en retrait.
- La liste montre les 50 mails les plus récents ; **Afficher les mails plus anciens**, en bas de la liste, charge les 50 suivants.
- Quai Mail garde une copie des dossiers et des en-têtes (cache) pour les afficher tout de suite au démarrage, puis les met à jour depuis le serveur. Si un serveur est injoignable, un bandeau le dit et le cache reste affiché.
- Ouvrir un mail le marque lu sur le serveur. Sur le serveur, Quai Mail ne change que lu / non lu, l'étoile, et range vos brouillons et mails envoyés : ni déplacement, ni suppression de vos mails à ce stade.

## Lire un mail

Choisissez un mail dans la liste : il s'affiche à droite. Les deux boutons en haut à droite le marquent **lu / non lu** et lui ajoutent ou retirent une **étoile**.

Le contenu d'un mail vient de l'extérieur : Quai Mail le traite comme tel.

- **Rien d'actif** : les scripts, cadres, formulaires et autres pièges sont retirés avant l'affichage, et l'affichage lui-même n'exécute aucun programme.
- **Images distantes bloquées** : les images hébergées sur Internet servent souvent à savoir quand et où vous lisez un mail (« pixel espion »). Elles ne sont pas chargées ; un bandeau l'indique, et **Afficher les images** les charge pour ce mail-là. Les images jointes au mail s'affichent toujours. Le réglage **Bloquer les images distantes** permet de toujours les afficher.
- **Liens** : un clic sur un lien n'ouvre rien directement. Quai Mail montre d'abord **l'adresse réelle** et le site visé (le texte d'un lien peut montrer une autre destination que la vraie, ce que font les arnaques) ; **Ouvrir** lance votre navigateur. Seuls les liens web et `mailto:` sont acceptés.
- Un mail sans version mise en forme s'affiche en texte, avec ses liens cliquables (même confirmation).

## Écrire

- **Nouveau message** (`Ctrl+N`), ou, depuis un mail ouvert, **Répondre**, **Répondre à tous** ou **Transférer** (boutons en haut à droite du mail).
- **De** : le compte d'envoi. **À** : une ou plusieurs adresses séparées par des virgules ; **Cc / Cci** affiche la copie et la copie cachée (les destinataires en Cci ne sont vus par personne).
- La réponse reprend l'objet (« Re: ») et cite le mail d'origine (lignes « > ») ; la réponse à tous ajoute les autres destinataires, jamais vous-même ; le transfert (« TR: ») reprend le texte et les pièces jointes du mail.
- **Joindre** ajoute des fichiers ; ils sont copiés dans le brouillon (déplacer ou effacer l'original ne change rien). Cliquez sur une pièce jointe pour la retirer.
- Les messages s'écrivent en texte simple.

**Envoyer** est le seul moyen d'envoyer un mail : il n'y a pas de raccourci clavier, rien ne part tout seul. Avant d'envoyer, Quai Mail vérifie les adresses (une seule erreur, et rien ne part). Le mail envoyé est rangé dans votre dossier Envoyés ; un bandeau confirme l'envoi. En cas d'échec (serveur injoignable, mot de passe refusé), le message reste ouvert, rien n'est perdu.

### Brouillons

Ce que vous écrivez est enregistré sur cet ordinateur toutes les 5 secondes, et copié dans le dossier Brouillons de votre compte toutes les 30 secondes (sans connexion, la copie attend ; l'heure du dernier enregistrement s'affiche en haut du message). Fermer le message (croix) demande **« Garder ce brouillon ? »** : **Garder** le conserve (sur l'ordinateur et dans Brouillons) et Quai Mail le rouvre au prochain démarrage ; **Supprimer** l'efface partout ; **Annuler** le laisse ouvert. Une mise à jour ou un plantage ne le perd jamais.

## Pièces jointes

Elles apparaissent sous les en-têtes du mail. Un clic **enregistre** la pièce jointe là où vous le choisissez ; Quai Mail ne l'ouvre jamais lui-même. Les fichiers qui peuvent lancer un programme (`.exe`, `.msi`, scripts, raccourcis, documents à macros…) sont signalés par une icône d'avertissement, et Quai Mail demande confirmation avant de les enregistrer.

## Réglages

| Réglage | Effet |
|---|---|
| **Thème** | Selon Windows, Clair ou Sombre, appliqué tout de suite |
| **Bloquer les images distantes** | activé par défaut : les images hébergées sur Internet ne sont chargées que sur votre demande, l'expéditeur ne sait pas quand vous lisez son mail |
| **Rechercher les mises à jour** | activé par défaut : au démarrage, Quai Mail demande à GitHub le numéro de la dernière version publiée |
| **Vider le cache** | efface les dossiers et en-têtes gardés sur cet ordinateur ; tout est relu sur vos serveurs, rien n'est perdu |

Les réglages s'appliquent et s'enregistrent immédiatement, sans bouton « Enregistrer ». La version installée est affichée en bas des réglages.

## Mises à jour

Quand une nouvelle version est publiée, un bandeau l'annonce en haut de la fenêtre. **Mettre à jour** :

1. télécharge l'installateur (bouton **Annuler** pendant le téléchargement) et vérifie son empreinte SHA-256 : un fichier qui ne correspond pas à celui publié est supprimé sans être exécuté ;
2. si un brouillon est en cours, demande **« Garder ce brouillon ? »** :
   - **Garder** : il est enregistré sur cet ordinateur et rouvert juste après la mise à jour ;
   - **Supprimer** : il est effacé ;
   - **Annuler** : rien ne se passe, la mise à jour attend (déjà téléchargée) que vous ayez fini ;
3. ferme Quai Mail, installe la nouvelle version sans rien demander, puis relance Quai Mail **au même endroit** : même compte, même dossier, même mail ouvert, brouillon rouvert.

Avec la version portable (zip), le bouton ouvre la page de la version sur GitHub.

## Fermer Quai Mail avec un message en cours

Quai Mail pose la même question **« Garder ce brouillon ? »**. Si l'enregistrement échoue (disque plein, dossier protégé), Quai Mail le dit et reste ouvert. Aucun mail n'est jamais envoyé sans que vous cliquiez sur **Envoyer**.

## Vos données

- Réglages : `%LOCALAPPDATA%\QuaiMail\settings.json` (thème, images distantes, mises à jour).
- Comptes : `%LOCALAPPDATA%\QuaiMail\accounts.json` (adresse, serveurs, identifiant ; **aucun mot de passe**). Mots de passe : coffre de Windows (Gestionnaire d'identification, « QuaiMail »).
- Cache : `%LOCALAPPDATA%\QuaiMail\cache\mail.db` (dossiers et en-têtes), effaçable à tout moment.
- Affichage des mails : `%LOCALAPPDATA%\QuaiMail\webview`, effacé à chaque démarrage et fermeture (aucun cookie gardé).
- Brouillons : `%LOCALAPPDATA%\QuaiMail\drafts`, un fichier par brouillon (et ses pièces jointes copiées), effacé une fois le mail envoyé ou le brouillon supprimé.
- Journaux : `%LOCALAPPDATA%\QuaiMail\logs` (7 jours). Jamais de mot de passe, d'objet, de contenu ni d'adresse de correspondant.
- Mise à jour en cours : `%LOCALAPPDATA%\QuaiMail\updates` (installateur, effacé au démarrage suivant ; `reopen.txt`, l'état de la fenêtre à restaurer, effacé dès qu'il a servi).
- Désinstallation : depuis « Applications installées » de Windows. Elle propose aussi d'effacer vos comptes, mots de passe (coffre de Windows), brouillons, cache, réglages et journaux ; vos mails restent sur vos serveurs.

## Connexions

Quai Mail ne se connecte qu'aux serveurs mail de vos comptes, aux sites des images distantes d'un mail seulement quand vous demandez **Afficher les images**, à GitHub pour savoir s'il existe une nouvelle version (rien sur vous ni sur vos mails ; désactivable dans les **Réglages**) et, au moment d'ajouter un compte, au site de votre fournisseur pour lire ses réglages publiés (seul le nom de domaine apparaît dans l'adresse demandée, jamais votre adresse mail). Aucune publicité, aucun pistage, aucun compte à créer.

## Limites actuelles

Version 0.4 : pas encore de recherche, de déplacement, de suppression ni de réception en direct (version 0.5), ni de connexion à Gmail / Outlook par le navigateur (0.6). Les messages s'écrivent en texte simple (pas de mise en forme). Un brouillon fait dans un autre logiciel se lit dans Brouillons mais ne se reprend pas encore. Les images distantes ne peuvent pas encore être autorisées pour un expéditeur.

Affichage : le moteur WebView2 de Microsoft (déjà présent sur Windows 11 et la plupart des Windows 10) est nécessaire. S'il manque, Quai Mail le dit et donne le lien officiel.
