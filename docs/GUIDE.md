# Guide de Quai Mail

> **Version 0.7 : mails signés avec OpenPGP (GPG).** Comptes IMAP / SMTP, lecture sûre, écriture, recherche, corbeille, réception en direct avec notifications, boîte de réception unifiée, liens mail, et maintenant la **signature OpenPGP** de vos mails avec votre clé GnuPG / Kleopatra. La connexion à Gmail et Outlook.com par le navigateur est annoncée (boutons grisés « à venir »).

## La fenêtre

Trois volets, de gauche à droite :

| Volet | Contenu |
|---|---|
| **Comptes et dossiers** | en haut, **Nouveau message** ; vos comptes mail et leurs dossiers (Boîte de réception, Envoyés…), avec le nombre de mails non lus ; en bas, **Ajouter un compte** et **Réglages** |
| **Mails** | les mails du dossier choisi, du plus récent au plus ancien : expéditeur, objet, date ; les non lus en gras |
| **Lecture** | le mail ouvert : expéditeur, destinataires, date, pièces jointes, puis son contenu ; ou le message que vous écrivez |

Tout se fait aussi au clavier : `Tab` passe d'un volet à l'autre, les flèches parcourent les dossiers et les mails, `F5` actualise les dossiers, `Ctrl+N` écrit un nouveau message, `Ctrl+F` cherche, `Suppr` supprime le mail choisi.

## Ajouter un compte

1. **Ajouter un compte** (en bas à gauche).
2. Saisissez votre **adresse mail** : Quai Mail cherche aussitôt les réglages du serveur :
   - pour les grands fournisseurs (Gmail, Yahoo, iCloud, Orange, Free, SFR, La Poste…), il les connaît déjà, sans rien demander à personne ;
   - sinon, il lit ceux que votre fournisseur publie lui-même sur son site (autoconfiguration) ;
   - à défaut, il propose les réglages habituels (`imap.` et `smtp.` suivis de votre domaine), à corriger si besoin dans **Réglages du serveur**.
3. Saisissez votre **mot de passe**. Gmail, Yahoo, iCloud et quelques autres demandent un **mot de passe d'application** : un mot de passe à part, créé dans les réglages de sécurité de votre compte chez eux. Quai Mail vous le signale.
4. **Ajouter** : Quai Mail se connecte pour vérifier (réception IMAP et envoi SMTP, **sans rien envoyer**). En cas d'échec, il dit pourquoi (serveur introuvable, mot de passe refusé, certificat non fiable…) et rien n'est enregistré.

Le mot de passe est gardé dans le **coffre de Windows** (Gestionnaire d'identification), jamais dans un fichier. Les connexions sont toujours chiffrées (SSL / TLS ou STARTTLS) et le certificat du serveur toujours vérifié : un certificat non fiable est refusé, sans possibilité de l'ignorer.

### Gmail, Outlook.com, Hotmail

- **Gmail** : utilisez un **mot de passe d'application** (compte Google, Sécurité, validation en deux étapes activée, « Mots de passe des applications »), saisi à la place de votre mot de passe habituel.
- **Outlook.com, Hotmail, Live** : Microsoft n'accepte plus les mots de passe pour ces adresses ; elles ne peuvent pas encore être ajoutées.

Les boutons **Se connecter avec Google** et **Se connecter avec Microsoft** sont visibles mais grisés, marqués **à venir** : la connexion par le navigateur (page officielle de Google ou de Microsoft, sans que Quai Mail voie votre mot de passe) arrive dans une prochaine version.

**Retirer un compte** : clic droit sur le compte (ou l'un de ses dossiers), **Retirer ce compte de Quai Mail…**, puis confirmer. Ses réglages, son mot de passe et son cache disparaissent de cet ordinateur ; vos mails restent sur le serveur.

## Dossiers et mails

- **Replier un compte** : cliquez sur sa ligne (la flèche ▾ devant l'adresse) ; ses dossiers disparaissent du volet et la ligne montre ses mails non lus (sans les Indésirables ni la Corbeille). Un autre clic les fait revenir. Au clavier : `Entrée` ou `Espace` sur la ligne du compte, `←` pour replier, `→` pour déplier, et `←` sur un dossier remonte à son compte. Chaque compte reste replié ou déplié d'un lancement à l'autre, mises à jour comprises ; le dossier affiché reste affiché quand on replie son compte.
- Les dossiers particuliers (Boîte de réception, Brouillons, Envoyés, Archives, Indésirables, Corbeille) viennent en premier, puis les autres, sous-dossiers en retrait.
- La liste montre les 50 mails les plus récents ; **Afficher les mails plus anciens**, en bas de la liste, charge les 50 suivants.
- Quai Mail garde une copie des dossiers et des en-têtes (cache) pour les afficher tout de suite au démarrage, puis les met à jour depuis le serveur. Si un serveur est injoignable, un bandeau le dit et le cache reste affiché.
- Ouvrir un mail le marque lu sur le serveur.
- Avec plusieurs comptes, **Toutes les boîtes de réception** (en haut) les réunit, du plus récent au plus ancien.
- **Réception en direct** : les nouveaux mails arrivent tout seuls en haut de la liste, sans rien actualiser. Si Quai Mail n'est pas au premier plan, une notification de Windows les annonce (expéditeur et objet) ; un clic dessus ramène à la boîte de réception. Les notifications se coupent dans les **Réglages**. Après une coupure (veille, réseau), Quai Mail se reconnecte tout seul et annonce ce qui est arrivé entre-temps.

## Chercher

Tapez quelques mots dans **Rechercher** (`Ctrl+F`) en haut de la liste, puis `Entrée` : Quai Mail cherche dans tous vos comptes et dossiers déjà ouverts sur cet ordinateur (objet, expéditeur, et texte des mails déjà lus), sans tenir compte des accents ni des majuscules, même sans connexion. **Chercher aussi sur le serveur** complète avec le texte de tous les mails du dossier d'où vous cherchez. Videz le champ pour revenir au dossier.

## Supprimer, déplacer

- **Supprimer** (touche `Suppr`, bouton corbeille en haut du mail, ou clic droit sur le mail) : le mail va dans la **corbeille** de votre compte. Depuis la corbeille (ou si le compte n'en a pas), la suppression est **définitive** : Quai Mail demande confirmation, « Annuler » étant le choix par défaut.
- **Déplacer** : clic droit sur le mail, **Déplacer vers**, puis le dossier.

## Lire un mail

Choisissez un mail dans la liste : il s'affiche à droite. Les deux boutons en haut à droite le marquent **lu / non lu** et lui ajoutent ou retirent une **étoile**.

Le contenu d'un mail vient de l'extérieur : Quai Mail le traite comme tel.

- **Rien d'actif** : les scripts, cadres, formulaires et autres pièges sont retirés avant l'affichage, et l'affichage lui-même n'exécute aucun programme.
- **Images distantes bloquées** : les images hébergées sur Internet servent souvent à savoir quand et où vous lisez un mail (« pixel espion »). Elles ne sont pas chargées ; un bandeau l'indique, et **Afficher les images** les charge pour ce mail-là. Les images jointes au mail s'affichent toujours. Le réglage **Bloquer les images distantes** permet de toujours les afficher.
- **Liens** : un clic sur un lien n'ouvre rien directement. Quai Mail montre d'abord **l'adresse réelle** et le site visé (le texte d'un lien peut montrer une autre destination que la vraie, ce que font les arnaques) ; **Ouvrir** lance votre navigateur. Seuls les liens web et `mailto:` sont acceptés.
- Un mail sans version mise en forme s'affiche en texte, avec ses liens cliquables (même confirmation).

### Expéditeurs sûrs et mode méfiant

Un mail dont l'expéditeur n'est pas dans vos **expéditeurs sûrs** est lu en **mode méfiant** : un bandeau le signale, ses images distantes restent bloquées et la confirmation d'un lien le rappelle. Quai Mail y relève aussi, sans rien envoyer sur Internet, les indices d'hameçonnage :

- **contrôles d'authentification en échec** (DMARC, ou SPF sans signature DKIM valable), tels que notés par votre serveur de réception : l'expéditeur est peut-être usurpé, le bandeau est rouge ;
- un **nom affiché** qui contient une autre adresse (« service@paypal.com » envoyé depuis une autre adresse) ;
- des **réponses** qui partiraient vers un autre domaine que celui de l'expéditeur ;
- un **domaine sosie** d'un domaine connu (`paypa1.com`, `exarnple.org`), ou écrit en caractères internationaux qui imitent un nom latin ;
- un **lien trompeur** (son texte annonce un site, il mène à un autre ; la confirmation le dit en premier) ou qui mène à une adresse IP.

**Faire confiance**, dans le bandeau, ajoute l'expéditeur à vos expéditeurs sûrs : cette adresse, ou tout son domaine (impossible pour une messagerie ouverte à tous comme gmail.com ou orange.fr, où n'importe qui crée une adresse). Leurs mails s'affichent ensuite normalement, images comprises. Les mails de vos propres adresses sont sûrs d'office. Un expéditeur sûr dont un mail échoue aux contrôles d'authentification perd la confiance pour ce mail-là : quelqu'un se fait peut-être passer pour lui. La liste se gère dans les **Réglages** (section **Expéditeurs sûrs**) et reste sur cet ordinateur.

## Écrire

- **Nouveau message** (`Ctrl+N`), ou, depuis un mail ouvert, **Répondre**, **Répondre à tous** ou **Transférer** (boutons en haut à droite du mail).
- **De** : le compte d'envoi. **À** : une ou plusieurs adresses séparées par des virgules ; **Cc / Cci** affiche la copie et la copie cachée (les destinataires en Cci ne sont vus par personne).
- La réponse reprend l'objet (« Re: ») et cite le mail d'origine (lignes « > ») ; la réponse à tous ajoute les autres destinataires, jamais vous-même ; le transfert (« TR: ») reprend le texte et les pièces jointes du mail.
- **Joindre** ajoute des fichiers ; ils sont copiés dans le brouillon (déplacer ou effacer l'original ne change rien). Cliquez sur une pièce jointe pour la retirer.
- Les messages s'écrivent en texte simple.

**Envoyer** est le seul moyen d'envoyer un mail : il n'y a pas de raccourci clavier, rien ne part tout seul. Avant d'envoyer, Quai Mail vérifie les adresses (une seule erreur, et rien ne part). Le mail envoyé est rangé dans votre dossier Envoyés ; un bandeau confirme l'envoi. En cas d'échec (serveur injoignable, mot de passe refusé), le message reste ouvert, rien n'est perdu.

### Brouillons

Ce que vous écrivez est enregistré sur cet ordinateur toutes les 5 secondes, et copié dans le dossier Brouillons de votre compte toutes les 30 secondes (sans connexion, la copie attend ; l'heure du dernier enregistrement s'affiche en haut du message). Fermer le message (croix) demande **« Garder ce brouillon ? »** : **Garder** le conserve (sur l'ordinateur et dans Brouillons) et Quai Mail le rouvre au prochain démarrage ; **Supprimer** l'efface partout ; **Annuler** le laisse ouvert. Une mise à jour ou un plantage ne le perd jamais.

## Signer vos mails (OpenPGP / GPG)

Une signature OpenPGP prouve à vos correspondants que le mail vient bien de vous et qu'il n'a pas été modifié en route (Thunderbird, Kleopatra, GnuPG, Mailvelope… la vérifient). Quai Mail signe avec **votre clé existante**, sans rien installer de plus.

1. **Exportez votre clé secrète** :
   - Kleopatra : clic droit sur votre clé, **Sauvegarder les clés secrètes…**, format armuré (`.asc`) ;
   - ligne de commande : `gpg --export-secret-keys --armor vous@exemple.fr > cle.asc`.
2. Quai Mail, **Réglages**, **Signature des mails (OpenPGP / GPG)**, **Importer une clé…**, choisissez ce fichier. La clé apparaît avec son identité, son empreinte et sa date d'expiration. Supprimez ensuite le fichier exporté.
3. Écrivez un message depuis l'adresse de la clé : le bouton **Signer** de la rédaction est activé d'office (cliquez dessus pour l'enlever, pour ce message). Sa bulle indique la clé utilisée.
4. **Envoyer** : Quai Mail demande la **phrase de passe** de la clé, signe, puis envoie. Une phrase refusée est redemandée ; **Annuler** n'envoie rien.

La phrase de passe n'est jamais enregistrée : Quai Mail la garde en mémoire jusqu'à sa fermeture (**Oublier les phrases de passe** dans les Réglages pour qu'elle soit redemandée). La clé importée reste protégée par cette phrase de passe, dans `%LOCALAPPDATA%\QuaiMail\pgp`. **Retirer cette clé** (Réglages) l'enlève de Quai Mail ; elle reste intacte dans GnuPG ou Kleopatra.

Format : PGP/MIME (le standard des clients mail), empreinte SHA-256. Clés RSA, Ed25519 (celles de GnuPG récent), ECDSA, DSA. Une clé expirée, révoquée, ou gardée seulement sur une carte à puce (YubiKey…) ne peut pas signer.

## Pièces jointes

Elles apparaissent sous les en-têtes du mail. Un clic **enregistre** la pièce jointe là où vous le choisissez ; Quai Mail ne l'ouvre jamais lui-même. Les fichiers qui peuvent lancer un programme (`.exe`, `.msi`, scripts, raccourcis, documents à macros…) sont signalés par une icône d'avertissement, et Quai Mail demande confirmation avant de les enregistrer.

Chaque pièce jointe enregistrée reçoit la **marque du web**, comme un fichier téléchargé avec un navigateur : Windows la fait vérifier par l'antivirus tout de suite, puis, quand vous l'ouvrez, SmartScreen contrôle les programmes et Office ouvre les documents en mode protégé. Si l'antivirus ou la stratégie de sécurité de Windows bloque le fichier, Quai Mail le retire et vous le dit. Sur un volume qui ne garde pas cette marque (clé USB en FAT32, certains partages réseau), le fichier est enregistré sans elle.

## Réglages

| Réglage | Effet |
|---|---|
| **Thème** | Selon Windows, Clair ou Sombre, appliqué tout de suite |
| **Bloquer les images distantes** | activé par défaut : les images hébergées sur Internet ne sont chargées que sur votre demande, l'expéditeur ne sait pas quand vous lisez son mail |
| **Rechercher les mises à jour** | activé par défaut : au démarrage, Quai Mail demande à GitHub le numéro de la dernière version publiée |
| **Notifications des nouveaux mails** | activé par défaut : une notification de Windows quand un mail arrive et que Quai Mail n'est pas au premier plan |
| **Vider le cache** | efface les dossiers et en-têtes gardés sur cet ordinateur ; tout est relu sur vos serveurs, rien n'est perdu |
| **Expéditeurs sûrs** | la liste des adresses et domaines lus sans le mode méfiant ; le bouton à droite d'une ligne la retire |

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

### Liens mail (« mailto: »)

Pour que les liens mail des pages web et des documents ouvrent Quai Mail : Paramètres de Windows, **Applications**, **Applications par défaut**, choisir **Quai Mail** pour **MAILTO** (Quai Mail ne se met jamais par défaut tout seul). Un lien ouvre alors un nouveau message prérempli dans la fenêtre de Quai Mail ; rien n'est envoyé sans **Envoyer**.

## Fermer Quai Mail avec un message en cours

Quai Mail pose la même question **« Garder ce brouillon ? »**. Si l'enregistrement échoue (disque plein, dossier protégé), Quai Mail le dit et reste ouvert. Aucun mail n'est jamais envoyé sans que vous cliquiez sur **Envoyer**.

## Vos données

- Réglages : `%LOCALAPPDATA%\QuaiMail\settings.json` (thème, images distantes, mises à jour).
- Comptes : `%LOCALAPPDATA%\QuaiMail\accounts.json` (adresse, serveurs, identifiant ; **aucun mot de passe**). Mots de passe : coffre de Windows (Gestionnaire d'identification, « QuaiMail »).
- Cache : `%LOCALAPPDATA%\QuaiMail\cache\mail.db` (dossiers et en-têtes), effaçable à tout moment.
- Affichage des mails : `%LOCALAPPDATA%\QuaiMail\webview`, effacé à chaque démarrage et fermeture (aucun cookie gardé).
- Expéditeurs sûrs : `%LOCALAPPDATA%\QuaiMail\trusted-senders.json` (adresses et domaines que vous avez ajoutés).
- Clés OpenPGP importées : `%LOCALAPPDATA%\QuaiMail\pgp`, protégées par leur phrase de passe (jamais enregistrée).
- Brouillons : `%LOCALAPPDATA%\QuaiMail\drafts`, un fichier par brouillon (et ses pièces jointes copiées), effacé une fois le mail envoyé ou le brouillon supprimé.
- Journaux : `%LOCALAPPDATA%\QuaiMail\logs` (7 jours). Jamais de mot de passe, d'objet, de contenu ni d'adresse de correspondant.
- Mise à jour en cours : `%LOCALAPPDATA%\QuaiMail\updates` (installateur, effacé au démarrage suivant ; `reopen.txt`, l'état de la fenêtre à restaurer, effacé dès qu'il a servi).
- Désinstallation : depuis « Applications installées » de Windows. Elle propose aussi d'effacer vos comptes, mots de passe (coffre de Windows), brouillons, cache, réglages et journaux ; vos mails restent sur vos serveurs.

## Connexions

Quai Mail ne se connecte qu'aux serveurs mail de vos comptes, aux sites des images distantes d'un mail seulement quand vous demandez **Afficher les images**, à GitHub pour savoir s'il existe une nouvelle version (rien sur vous ni sur vos mails ; désactivable dans les **Réglages**) et, au moment d'ajouter un compte, au site de votre fournisseur pour lire ses réglages publiés (seul le nom de domaine apparaît dans l'adresse demandée, jamais votre adresse mail). Aucune publicité, aucun pistage, aucun compte à créer.

## Limites actuelles

Version 0.7 : la signature OpenPGP ne chiffre pas les mails et ne vérifie pas encore les signatures reçues (la signature d'un mail reçu apparaît comme une pièce jointe `signature.asc`) ; pas de clé sur carte à puce. Pas encore de connexion à Gmail / Outlook.com par le navigateur (boutons grisés « à venir ») ; Gmail fonctionne avec un mot de passe d'application, Outlook.com et Hotmail pas encore. La réception en direct surveille la boîte de réception (pas les autres dossiers). Les messages s'écrivent en texte simple (pas de mise en forme). Un brouillon fait dans un autre logiciel se lit dans Brouillons mais ne se reprend pas encore. Les images distantes s'autorisent pour un expéditeur en le déclarant sûr (pas de réglage « images seulement »).

Affichage : le moteur WebView2 de Microsoft (déjà présent sur Windows 11 et la plupart des Windows 10) est nécessaire. S'il manque, Quai Mail le dit et donne le lien officiel.
