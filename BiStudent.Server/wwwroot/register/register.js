const $ = id => document.getElementById(id);
const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
const poses = ['Смотрите прямо в камеру', 'Слегка поверните голову влево', 'Слегка поверните голову вправо'];
let code = '';
let group = null;
let selectedId = null;
let captures = [null, null, null];
let fileReads = [0, 0, 0];
let pendingReads = [0, 0, 0];
let stream = null;
let cameraStarting = false;
let captureIndex = 0;
let retaking = false;
let sending = false;
let currentStep = 1;
let cameraAttempt = 0;
let registrationGeneration = 0;
let language = 'ru';
let manualLanguage = null;
try {
  const savedLanguage = localStorage.getItem('bistudent.register.lang');
  if (savedLanguage === 'ru' || savedLanguage === 'kk') manualLanguage = savedLanguage;
} catch {}
const strings = {
  ru: {
    title: 'Регистрация лица — BiStudent', header: 'Регистрация лица для учёта посещаемости', stepsLabel: 'Шаги регистрации', languageLabel: 'Язык',
    step1: '1 Код', step2: '2 Студент', step3: '3 Снимки', step4: '4 Готово',
    codeTitle: 'Введите код группы', codeHint: 'Код выдаёт преподаватель, например K7PM-Q2XA', codeLabel: 'Код группы', continue: 'Продолжить',
    studentTitle: 'Выберите себя в группе', search: 'Поиск по ФИО', searchPlaceholder: 'Начните вводить фамилию', studentsLabel: 'Студенты группы',
    replacementWarning: 'Новые снимки заменят прежние после подтверждения преподавателем.',
    consent: 'Я подтверждаю, что выбрал(а) себя, и даю согласие на обработку изображения моего лица (биометрических персональных данных) для учёта посещаемости. Снимки видит только преподаватель; полные кадры не сохраняются.',
    next: 'Далее', photosTitle: 'Сделайте три снимка', photosHint: 'Хорошее освещение спереди, без очков от солнца и масок, лицо целиком в рамке.', cameraLabel: 'Камера', capture: 'Сделать снимок',
    fileNotice: 'Браузер не дал доступ к камере на этой странице — сделайте три снимка через камеру телефона.',
    filePose0: '1. Смотрите прямо в камеру', filePose1: '2. Слегка поверните голову влево', filePose2: '3. Слегка поверните голову вправо',
    thumbnailsLabel: 'Сделанные снимки', back: 'Назад', submit: 'Отправить', sentTitle: 'Снимки отправлены', another: 'Зарегистрировать другого студента', footer: 'Данные передаются только на сервер учебного заведения',
    poses, pending: 'На проверке', registered: 'Лицо зарегистрировано',
    shot: 'Снимок {0}', retake: 'Переснять', sending: 'Отправляем…', camera: 'Камера {0}', expires: 'Код действует до {0}',
    invalidCode: 'Введите восьмизначный код группы.', noConnection: 'Нет связи с сервером. Проверьте подключение и повторите попытку.',
    rateLimit: 'Слишком много попыток — подождите минуту.', unexpected: 'Сервер вернул неожиданный ответ. Повторите попытку.', failed: 'Не удалось выполнить запрос.', readFile: 'Не удалось прочитать снимок. Выберите другой файл.'
  },
  kk: {
    title: 'Бетті тіркеу — BiStudent', header: 'Сабаққа қатысуды есепке алу үшін бетті тіркеу', stepsLabel: 'Тіркеу қадамдары', languageLabel: 'Тіл',
    step1: '1 Код', step2: '2 Студент', step3: '3 Суреттер', step4: '4 Дайын',
    codeTitle: 'Топ кодын енгізіңіз', codeHint: 'Кодты оқытушы береді, мысалы K7PM-Q2XA', codeLabel: 'Топ коды', continue: 'Жалғастыру',
    studentTitle: 'Топтан өзіңізді таңдаңыз', search: 'Аты-жөні бойынша іздеу', searchPlaceholder: 'Тегіңізді тере бастаңыз', studentsLabel: 'Топ студенттері',
    replacementWarning: 'Жаңа суреттер оқытушы растағаннан кейін бұрынғыларын алмастырады.',
    consent: 'Мен өзімді таңдағанымды растаймын және сабаққа қатысуды есепке алу үшін бетімнің суретін (биометриялық дербес деректерімді) өңдеуге келісім беремін. Суреттерді тек оқытушы көреді; толық кадрлар сақталмайды.',
    next: 'Келесі', photosTitle: 'Үш сурет түсіріңіз', photosHint: 'Жарық бетіңізге алдынан түссін, күннен қорғайтын көзілдірік пен бетперде кимеңіз, бетіңіз кадрға толық сыйсын.', cameraLabel: 'Камера', capture: 'Сурет түсіру',
    fileNotice: 'Браузер осы бетте камераны пайдалануға рұқсат бермеді — телефон камерасымен үш сурет түсіріңіз.',
    filePose0: '1. Камераға тіке қараңыз', filePose1: '2. Басыңызды сәл солға бұрыңыз', filePose2: '3. Басыңызды сәл оңға бұрыңыз',
    thumbnailsLabel: 'Түсірілген суреттер', back: 'Артқа', submit: 'Жіберу', sentTitle: 'Суреттер жіберілді', another: 'Басқа студентті тіркеу', footer: 'Деректер тек оқу орнының серверіне жіберіледі',
    poses: ['Камераға тіке қараңыз', 'Басыңызды сәл солға бұрыңыз', 'Басыңызды сәл оңға бұрыңыз'], pending: 'Тексерілуде', registered: 'Бет тіркелген',
    shot: 'Сурет {0}', retake: 'Қайта түсіру', sending: 'Жіберілуде…', camera: 'Камера {0}', expires: 'Кодтың жарамдылық мерзімі: {0}',
    invalidCode: 'Топтың сегіз таңбалы кодын енгізіңіз.', noConnection: 'Сервермен байланыс жоқ. Қосылымды тексеріп, қайталап көріңіз.',
    rateLimit: 'Тым көп әрекет жасалды — бір минут күтіңіз.', unexpected: 'Сервер күтпеген жауап қайтарды. Қайталап көріңіз.', failed: 'Сұрауды орындау мүмкін болмады.', readFile: 'Суретті оқу мүмкін болмады. Басқа файлды таңдаңыз.',
    shotProblem: 'Сурет {0}: {1}', similarProblem: 'Суреттер {0} және {1} бір-біріне тым ұқсас — басыңызды сәл бұрыңыз'
  }
};
const serverMessages = {
  'Код недействителен или срок его действия истёк. Попросите у преподавателя новый код.': 'Код жарамсыз немесе оның мерзімі өтіп кеткен. Оқытушыдан жаңа код сұраңыз.',
  'Студент не найден в группе.': 'Студент топтан табылмады.',
  'Студент не найден.': 'Студент табылмады.',
  'Нужно согласие на обработку биометрических данных.': 'Биометриялық деректерді өңдеуге келісім қажет.',
  'Нужно ровно три снимка.': 'Дәл үш сурет қажет.',
  'Не удалось прочитать снимок.': 'Суретті оқу мүмкін болмады.',
  'Снимок слишком большой.': 'Сурет тым үлкен.',
  'Не удалось прочитать JPEG.': 'JPEG файлын оқу мүмкін болмады.',
  'Некоторые снимки не подходят — переснимите их': 'Кейбір суреттер талапқа сай емес — оларды қайта түсіріңіз',
  'Распознавание недоступно.': 'Бетті тану қызметі қолжетімсіз.',
  'Снимки отправлены. Преподаватель проверит их и подтвердит регистрацию.': 'Суреттер жіберілді. Оқытушы оларды тексеріп, тіркеуді растайды.',
  'Данные одновременно изменил другой запрос — обновите и повторите.': 'Деректерді басқа сұрау бір мезгілде өзгертті — бетті жаңартып, қайталап көріңіз.',
  'В кадре нет лица': 'Кадрда бет жоқ',
  'В кадре несколько лиц — оставьте одного человека': 'Кадрда бірнеше бет бар — бір адам ғана қалсын',
  'Лицо слишком далеко — подойдите ближе к камере': 'Бет тым алыс — камераға жақындаңыз',
  'Кадр размыт — не двигайтесь': 'Кадр бұлыңғыр — қозғалмаңыз',
  'Слишком темно — добавьте света': 'Тым қараңғы — жарықты күшейтіңіз',
  'Лицо засвечено — уберите яркий свет за спиной': 'Бетіңіз шамадан тыс жарықтанған — артыңыздағы қатты жарықты азайтыңыз',
  'Для образца нужно одно лицо в кадре.': 'Үлгі үшін кадрда бір ғана бет болуы керек.'
};

function formatCode(value) {
  const clean = [...value.toUpperCase()].filter(char => alphabet.includes(char)).slice(0, 8).join('');
  return clean.length > 4 ? `${clean.slice(0, 4)}-${clean.slice(4)}` : clean;
}

function t(key, ...args) {
  return strings[language][key].replace(/\{(\d+)\}/g, (_, index) => args[index]);
}

function languageOfGroup(name) {
  return name.split('-').some(segment => /^\d+[КкKk]$/.test(segment.trim())) ? 'kk' : 'ru';
}

function translateServer(message) {
  if (language !== 'kk' || !message) return message;
  if (Object.hasOwn(serverMessages, message)) return serverMessages[message];
  const shot = /^Снимок (\d+): (.+)$/.exec(message);
  if (shot) {
    if (Object.hasOwn(serverMessages, shot[2])) return t('shotProblem', shot[1], serverMessages[shot[2]]);
  }
  const similar = /^Снимки (\d+) и (\d+) почти одинаковые — немного поверните голову$/.exec(message);
  if (similar) return t('similarProblem', similar[1], similar[2]);
  return message;
}

function localError(key) {
  const error = new Error(t(key));
  error.i18nKey = key;
  return error;
}

function serverError(message) {
  const error = new Error(translateServer(message));
  error.serverMessage = message;
  return error;
}

function clearError(id) {
  const element = $(id);
  delete element.dataset.i18nError;
  delete element.dataset.serverMessage;
  element.textContent = '';
}

function setError(id, error) {
  const element = $(id);
  clearError(id);
  if (error.i18nKey) element.dataset.i18nError = error.i18nKey;
  if (error.serverMessage) element.dataset.serverMessage = error.serverMessage;
  element.textContent = error.i18nKey ? t(error.i18nKey) :
    error.serverMessage ? translateServer(error.serverMessage) : error.message;
}

function updateExpires() {
  if (!group) return;
  const expires = new Date(group.expiresAt);
  $('expires').textContent = t('expires', expires.toLocaleString(language === 'kk' ? 'kk-KZ' : 'ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }));
}

function setLanguage(lang) {
  language = lang === 'kk' ? 'kk' : 'ru';
  document.documentElement.lang = language;
  document.title = t('title');
  document.querySelectorAll('[data-i18n]').forEach(element => { element.textContent = t(element.dataset.i18n); });
  document.querySelectorAll('[data-i18n-placeholder]').forEach(element => { element.placeholder = t(element.dataset.i18nPlaceholder); });
  document.querySelectorAll('[data-i18n-aria-label]').forEach(element => { element.setAttribute('aria-label', t(element.dataset.i18nAriaLabel)); });
  document.querySelectorAll('[data-i18n-error]').forEach(element => { element.textContent = t(element.dataset.i18nError); });
  document.querySelectorAll('[data-server-message]').forEach(element => { element.textContent = translateServer(element.dataset.serverMessage); });
  $('camera-select').querySelectorAll('[data-i18n-camera]').forEach(option => { option.textContent = t('camera', option.dataset.i18nCamera); });
  $('language-ru').setAttribute('aria-pressed', language === 'ru');
  $('language-kk').setAttribute('aria-pressed', language === 'kk');
  $('submit').textContent = t(sending ? 'sending' : 'submit');
  if (group) {
    $('group-name').textContent = group.groupName;
    renderStudents();
    updateExpires();
  }
  updateCaptures();
}

function showStep(number) {
  if (sending && number !== 4) return;
  if (currentStep === 3 && number !== 3) {
    registrationGeneration++;
    pendingReads.fill(0);
  }
  currentStep = number;
  for (let index = 1; index <= 4; index++) {
    $(`step-${index}`).hidden = index !== number;
    document.querySelector(`[data-step-label="${index}"]`).classList.toggle('active', index === number);
  }
  if (number !== 3) stopCamera();
  updateButtons();
  window.scrollTo(0, 0);
}

function stopCamera() {
  cameraAttempt++;
  cameraStarting = false;
  if (stream) stream.getTracks().forEach(track => track.stop());
  stream = null;
  $('video').srcObject = null;
}

async function api(path, body, generation) {
  let response;
  try {
    response = await fetch(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    if (generation !== registrationGeneration) return null;
  } catch {
    throw localError('noConnection');
  }
  if (response.status === 429) throw localError('rateLimit');
  let data;
  try {
    data = await response.json();
    if (generation !== registrationGeneration) return null;
  }
  catch { throw localError('unexpected'); }
  if (!response.ok) throw data.message ? serverError(data.message) : localError('failed');
  return data;
}

function updateButtons() {
  $('student-next').disabled = sending || selectedId === null || !$('consent').checked;
  $('students').querySelectorAll('input').forEach(input => { input.disabled = sending; });
  $('back').disabled = sending;
  $('another').disabled = sending;
  $('camera-select').disabled = sending;
  $('submit').disabled = sending || cameraStarting || pendingReads.some(count => count) || currentStep !== 3 || captures.some(image => !image);
  $('capture').disabled = sending || currentStep !== 3 || !stream ||
    (captures[captureIndex] !== null && !retaking);
  for (let index = 0; index < 3; index++) $(`file-${index}`).disabled = sending;
  $('thumbnails').querySelectorAll('button').forEach(button => { button.disabled = sending; });
}

function updateNext() {
  updateButtons();
  const selected = group?.students.find(student => student.id === selectedId);
  $('replacement-warning').hidden = !selected || selected.state === 'None';
}

function renderStudents() {
  const list = $('students');
  list.replaceChildren();
  const query = $('student-search').value.trim().toLocaleLowerCase('ru');
  for (const student of group.students.filter(item => item.fullName.toLocaleLowerCase('ru').includes(query))) {
    const label = document.createElement('label');
    label.className = 'student-card';
    const radio = document.createElement('input');
    radio.type = 'radio';
    radio.name = 'student';
    radio.value = student.id;
    radio.checked = student.id === selectedId;
    radio.addEventListener('change', () => {
      if (selectedId !== student.id) registrationGeneration++;
      selectedId = student.id;
      updateNext();
    });
    const name = document.createElement('span');
    name.className = 'name';
    name.textContent = student.fullName;
    label.append(radio, name);
    if (student.state !== 'None') {
      const badge = document.createElement('span');
      badge.className = `badge${student.state === 'Pending' ? ' pending' : ''}`;
      badge.textContent = t(student.state === 'Pending' ? 'pending' : 'registered');
      label.append(badge);
    }
    list.append(label);
  }
}

async function checkCode() {
  const generation = ++registrationGeneration;
  clearError('code-error');
  code = formatCode($('code').value);
  $('code').value = code;
  if (code.replace('-', '').length !== 8) {
    setError('code-error', localError('invalidCode'));
    return;
  }
  $('code-button').disabled = true;
  try {
    const checkedGroup = await api('/api/public/enrollment/check', { code }, generation);
    if (generation !== registrationGeneration) return;
    group = checkedGroup;
    selectedId = null;
    $('consent').checked = false;
    $('student-search').value = '';
    setLanguage(manualLanguage || languageOfGroup(group.groupName));
    updateNext();
    showStep(2);
  } catch (error) {
    if (generation === registrationGeneration) setError('code-error', error);
  } finally {
    if (generation === registrationGeneration) $('code-button').disabled = false;
  }
}

function updateCaptures() {
  const list = $('thumbnails');
  list.replaceChildren();
  for (let index = 0; index < 3; index++) {
    const item = document.createElement('div');
    item.className = 'thumb';
    if (captures[index]) {
      const image = document.createElement('img');
      image.src = `data:image/jpeg;base64,${captures[index]}`;
      image.alt = t('shot', index + 1);
      item.append(image);
      const retake = document.createElement('button');
      retake.type = 'button';
      retake.className = 'secondary';
      retake.textContent = t('retake');
      retake.addEventListener('click', () => {
        if (sending) return;
        if (stream) {
          captureIndex = index;
          retaking = true;
          updatePose();
          updateButtons();
          $('capture').focus();
        } else {
          const input = $(`file-${index}`);
          input.value = '';
          input.click();
        }
      });
      item.append(retake);
    } else {
      const placeholder = document.createElement('div');
      placeholder.className = 'thumb-placeholder';
      placeholder.textContent = t('shot', index + 1);
      item.append(placeholder);
    }
    list.append(item);
  }
  updateButtons();
  updatePose();
}

function updatePose() {
  $('pose').textContent = `${captureIndex + 1}. ${strings[language].poses[captureIndex]}`;
}

function scaledJpeg(source, width, height) {
  const ratio = Math.min(1, 1280 / Math.max(width, height));
  const canvas = document.createElement('canvas');
  canvas.width = Math.round(width * ratio);
  canvas.height = Math.round(height * ratio);
  canvas.getContext('2d').drawImage(source, 0, 0, canvas.width, canvas.height);
  return canvas.toDataURL('image/jpeg', 0.9).split(',')[1];
}

async function startCamera(deviceId) {
  stopCamera();
  const generation = registrationGeneration;
  const attempt = cameraAttempt;
  if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia) {
    $('camera-mode').hidden = true;
    $('file-mode').hidden = false;
    updateCaptures();
    return;
  }
  cameraStarting = true;
  updateButtons();
  try {
    const nextStream = await navigator.mediaDevices.getUserMedia({
      video: deviceId ? { deviceId: { exact: deviceId }, width: { ideal: 1280 }, height: { ideal: 720 } }
        : { facingMode: 'user', width: { ideal: 1280 }, height: { ideal: 720 } },
      audio: false
    });
    if (generation !== registrationGeneration || attempt !== cameraAttempt || currentStep !== 3 || sending) {
      nextStream.getTracks().forEach(track => track.stop());
      return;
    }
    stream = nextStream;
    $('video').srcObject = stream;
    $('camera-mode').hidden = false;
    $('file-mode').hidden = true;
    const devices = (await navigator.mediaDevices.enumerateDevices()).filter(item => item.kind === 'videoinput');
    if (generation !== registrationGeneration || attempt !== cameraAttempt || currentStep !== 3 || sending) return;
    $('camera-select').replaceChildren();
    devices.forEach((device, index) => {
      const option = document.createElement('option');
      option.value = device.deviceId;
      if (!device.label) option.dataset.i18nCamera = index + 1;
      option.textContent = device.label || t('camera', index + 1);
      $('camera-select').append(option);
    });
    $('camera-select').value = stream.getVideoTracks()[0].getSettings().deviceId || '';
    $('camera-select').hidden = $('camera-label').hidden = devices.length <= 1;
  } catch {
    if (generation !== registrationGeneration || attempt !== cameraAttempt || currentStep !== 3 || sending) return;
    stopCamera();
    $('camera-mode').hidden = true;
    $('file-mode').hidden = false;
  } finally {
    if (attempt === cameraAttempt) cameraStarting = false;
    updateCaptures();
  }
}

$('code').addEventListener('input', event => { event.target.value = formatCode(event.target.value); });
$('code-form').addEventListener('submit', event => { event.preventDefault(); checkCode(); });
$('student-search').addEventListener('input', renderStudents);
$('consent').addEventListener('change', updateNext);
$('student-next').addEventListener('click', () => {
  if (sending) return;
  captures = [null, null, null];
  pendingReads.fill(0);
  for (let index = 0; index < 3; index++) $(`file-${index}`).value = '';
  captureIndex = 0;
  retaking = false;
  clearError('submit-error');
  $('problems').replaceChildren();
  updateCaptures();
  showStep(3);
  startCamera();
});
$('camera-select').addEventListener('change', event => { if (!sending) startCamera(event.target.value); });
$('capture').addEventListener('click', () => {
  if (sending || currentStep !== 3 || !stream) return;
  const video = $('video');
  if (!video.videoWidth || !video.videoHeight) return;
  fileReads[captureIndex]++;
  captures[captureIndex] = scaledJpeg(video, video.videoWidth, video.videoHeight);
  retaking = false;
  captureIndex = captures.findIndex(image => !image);
  if (captureIndex < 0) captureIndex = 2;
  updateCaptures();
});
for (let index = 0; index < 3; index++) {
  $(`file-${index}`).addEventListener('change', async event => {
    if (sending || currentStep !== 3) return;
    const file = event.target.files?.[0];
    if (!file) return;
    const generation = registrationGeneration;
    const read = ++fileReads[index];
    pendingReads[index]++;
    updateButtons();
    try {
      const bitmap = await createImageBitmap(file);
      try {
        if (read !== fileReads[index] || generation !== registrationGeneration || sending || currentStep !== 3) return;
        captures[index] = scaledJpeg(bitmap, bitmap.width, bitmap.height);
        updateCaptures();
        clearError('submit-error');
      } finally {
        bitmap.close();
      }
    } catch {
      if (read === fileReads[index] && generation === registrationGeneration && !sending && currentStep === 3)
        setError('submit-error', localError('readFile'));
    } finally {
      if (generation === registrationGeneration) pendingReads[index]--;
      updateButtons();
    }
  });
}
$('back').addEventListener('click', () => showStep(2));
$('submit').addEventListener('click', async () => {
  if (sending || cameraStarting || pendingReads.some(count => count) || captures.some(image => !image)) return;
  const studentId = selectedId;
  const studentName = group.students.find(student => student.id === studentId).fullName;
  const images = [...captures];
  const generation = ++registrationGeneration;
  sending = true;
  updateButtons();
  $('submit').textContent = t('sending');
  clearError('submit-error');
  $('problems').replaceChildren();
  try {
    const result = await api('/api/public/enrollment/submit', { code, studentId, images, consent: true }, generation);
    if (generation !== registrationGeneration) return;
    if (!result.ok) {
      setError('submit-error', serverError(result.message));
      for (const problem of result.problems || []) {
        const item = document.createElement('li');
        item.dataset.serverMessage = problem;
        item.textContent = translateServer(problem);
        $('problems').append(item);
      }
      return;
    }
    $('completed-student').textContent = studentName;
    $('completed-message').dataset.serverMessage = result.message;
    $('completed-message').textContent = translateServer(result.message);
    sending = false;
    $('submit').textContent = t('submit');
    showStep(4);
  } catch (error) {
    if (generation === registrationGeneration) setError('submit-error', error);
  } finally {
    if (generation === registrationGeneration) {
      sending = false;
      $('submit').textContent = t('submit');
      updateCaptures();
      if (currentStep === 3 && !$('camera-mode').hidden && !stream)
        startCamera($('camera-select').value);
    }
  }
});
$('another').addEventListener('click', async () => {
  if (sending) return;
  const generation = ++registrationGeneration;
  selectedId = null;
  captures = [null, null, null];
  pendingReads.fill(0);
  for (let index = 0; index < 3; index++) $(`file-${index}`).value = '';
  $('consent').checked = false;
  $('student-search').value = '';
  clearError('student-error');
  try {
    const checkedGroup = await api('/api/public/enrollment/check', { code }, generation);
    if (generation !== registrationGeneration) return;
    group = checkedGroup;
    $('group-name').textContent = group.groupName;
    updateExpires();
    clearError('student-error');
  }
  catch (error) {
    if (generation === registrationGeneration) setError('student-error', error);
  }
  if (generation !== registrationGeneration) return;
  renderStudents();
  updateNext();
  showStep(2);
});
for (const lang of ['ru', 'kk']) $(`language-${lang}`).addEventListener('click', () => {
  manualLanguage = lang;
  try { localStorage.setItem('bistudent.register.lang', lang); } catch {}
  setLanguage(lang);
});
setLanguage(manualLanguage || 'ru');
showStep(1);
const queryCode = new URLSearchParams(location.search).get('code');
if (queryCode) { $('code').value = formatCode(queryCode); checkCode(); }
