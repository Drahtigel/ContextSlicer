# 🌐 Google Docs API Connection Guide / Руководство по подключению Google Docs API

Select your language below / Выберите язык ниже:

* [🌐 English Version](#-english-version)
* [🌐 Русская версия](#-русская-версия)

---

## 🌐 English Version

Connecting Google Documents to the utility might seem complicated due to technical terms, but the entire process takes less than **3 minutes**. 

The program requires a special access key (a file in `.json` format) to communicate with the Google Drive API securely. Follow these **4 simple steps**:

<details>
<summary><b>Step 1. Create a Project in Google Cloud</b></summary>

1. Open the [Google Cloud Console](https://google.com).
2. Log in using your regular Google account.
3. Click on the project dropdown list at the top left of the screen and select **"New Project"**.
4. Enter any name you like (e.g., `MyNovelSlicer`) and click **"Create"**. Wait a few seconds for the project initialization to complete.
</details>

<details>
<summary><b>Step 2. Enable the Google Docs API</b></summary>

1. Make sure your newly created project is selected in the top menu.
2. In the left navigation sidebar, click on **"APIs & Services"** ➡️ **"Library"**.
3. In the search bar, type: `Google Docs API`.
4. Click on the found API card and hit the large blue **"Enable"** button.
</details>

<details>
<summary><b>Step 3. Create a Service Account and Download the JSON Key</b></summary>

1. Open the left menu again and navigate to **"IAM & Admin"** ➡️ **"Service Accounts"**.
2. Click **"+ Create Service Account"** at the top.
3. Enter any name (e.g., `slicer-reader`) and click **"Create and Continue"**, then click **"Done"** (you can safely skip the optional role selection steps).
4. In the list, you will see your new account, which looks exactly like an email address (e.g., `slicer-reader@://gserviceaccount.com`).
5. Click on this email address, then switch to the **"Keys"** tab at the top.
6. Click **"Add Key"** ➡️ **"Create new key"**.
7. Ensure **JSON** is selected as the format and click **"Create"**.
8. Your browser will automatically download a key file (e.g., `my-project-xxxx.json`). **Keep this file secure and private!**
</details>

<details>
<summary><b>Step 4. Configure the Utility and Your Google Document</b></summary>

1. Return to the application, open your project settings, and click **"Browse"** next to the *Google API Key* field. Select your downloaded `.json` file. The utility will securely import it.
2. Click the **"Copy Email"** button (the 📋 clipboard icon next to the key selection) to copy the service account's technical email address.
3. Open your book in Google Docs via your browser, and click the **"Share"** button in the top right corner.
4. Paste the copied email address into the input field, set its permission level to **"Viewer"**, and click "Send".
5. Copy the full URL link to your document from the browser's address bar, paste it into the *Data Source* field in the utility, and click **"Accept"**!
</details>

---

## 🌐 Русская версия

Подключение Google Документов к утилите может показаться сложным из-за обилия терминов, но весь процесс занимает не более **3 минут**. 

Программе нужен специальный «ключ доступа» (файл в формате `.json`), который сообщает Google Drive API, что утилите разрешено читать этот файл. Пройдите **4 простых шага**:

<details>
<summary><b>Шаг 1. Создание проекта в Google Cloud</b></summary>

1. Перейдите на официальный сайт [Google Cloud Console](https://google.com).
2. Зайдите под своей обычной учетной записью Google.
3. In самом верху экрана нажмите на выпадающий список проектов и выберите **«New Project»** (Создать проект).
4. Введите любое имя (например, `MyNovelSlicer`) и нажмите **«Create»**. Подождите несколько секунд, пока проект инициализируется.
</details>

<details>
<summary><b>Шаг 2. Включение Google Docs API</b></summary>

1. Убедитесь, что в верхнем меню выбран ваш только что созданный проект.
2. В левой панели нажмите на раздел **«APIs & Services»** (API и службы) ➡️ **«Library»** (Библиотека).
3. В строке поиска введите: `Google Docs API`.
4. Нажмите на найденную карточку API и кликните крупную синюю кнопку **«Enable»** (Включить).
</details>

<details>
<summary><b>Шаг 3. Создание Сервисного Аккаунта и скачивание JSON-ключа</b></summary>

1. Снова откройте левое меню и перейдите в раздел **«IAM & Admin»** (Управление доступом) ➡️ **«Service Accounts»** (Сервисные аккаунты).
2. Вверху нажмите **«+ Create Service Account»**.
3. Введите любое имя (например, `slicer-reader`) и нажмите **«Create and Continue»**, а затем **«Done»** (шаги с выбором ролей можно просто пропустить).
4. В появившемся списке вы увидите созданный аккаунт, похожий на Email (например, `slicer-reader@://gserviceaccount.com`).
5. Кликните на этот Email, перейдите на вкладку **«Keys»** (Ключи) вверху.
6. Нажмите **«Add Key»** ➡️ **«Create new key»**.
7. Убедитесь, что выбран тип **JSON**, и нажмите **«Create»**. 
8. Браузер автоматически скачает на ваш компьютер файл ключа (например, `my-project-xxxx.json`). **Никому не показывайте этот файл!**
</details>

<details>
<summary><b>Шаг 4. Настройка утилиты и документа</b></summary>

1. Вернитесь в наше приложение, откройте настройки проекта и в строке *Google API Key* нажмите кнопку **«Обзор»**. Выберите скачанный `.json` файл. Утилита сама импортирует его в безопасное хранилище.
2. Нажмите кнопку **«Скопировать Email»** (иконка 📋 рядом с выбором ключа) — технический адрес скопируется в ваш буфер обмена.
3. Теперь откройте в браузере вашу книгу в Google Документах, нажмите кнопку **«Поделиться»** в правом верхнем углу.
4. Вставьте скопированный Email из буфера обмена в поле ввода, установите ему права **«Читатель»** (Viewer) и нажмите «Отправить».
5. Скопируйте ссылку на ваш документ из адресной строки браузера, вставьте её в утилите в поле *Источник данных* и нажмите **«Принять»**!
</details>
