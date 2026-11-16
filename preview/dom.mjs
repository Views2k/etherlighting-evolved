export function node(tag, className = '', text) {
  const element = document.createElement(tag);
  if (className) element.className = className;
  if (text !== undefined) element.textContent = text;
  return element;
}

export function input(value = '', type = 'text') {
  const element = node('input'); element.type = type; element.value = value; return element;
}

export function field(label, control, help) {
  const wrapper = node('label', 'field');
  wrapper.append(node('span', '', label), control);
  if (help) wrapper.append(node('small', '', help));
  return wrapper;
}

export function select(options, value) {
  const element = node('select');
  for (const [key, label] of options) {
    const option = node('option', '', label); option.value = key; element.append(option);
  }
  element.value = value ?? options[0]?.[0] ?? '';
  return element;
}
