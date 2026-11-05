(() => {
  let theme='evolved';
  try{
    const saved=localStorage.getItem('ee.theme.v1');
    if(typeof saved==='string'&&/^[a-z]{1,16}$/.test(saved))theme=saved;
  }catch{}
  document.documentElement.dataset.theme=theme;
})();
