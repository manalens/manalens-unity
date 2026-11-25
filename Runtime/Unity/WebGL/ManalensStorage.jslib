// Synchronous browser storage for the Manalens Unity SDK on WebGL (BrowserStorage.cs).
// PlayerPrefs reach IndexedDB only a moment after PlayerPrefs.Save(), so a reload in
// that moment lost the state (a new anonymous ID). localStorage writes at once.
mergeInto(LibraryManager.library, {
  // 1 when localStorage can be read and written (not in some private modes or sandboxed iframes).
  ManalensStorageAvailable: function () {
    try {
      var probe = '__manalens_probe__';
      window.localStorage.setItem(probe, '1');
      window.localStorage.removeItem(probe);
      return 1;
    } catch (e) {
      return 0;
    }
  },

  // "1" + the value, "0" when the key is missing, "2" when the read failed.
  ManalensStorageGet: function (key) {
    var result;
    try {
      var value = window.localStorage.getItem(UTF8ToString(key));
      result = value === null ? '0' : '1' + value;
    } catch (e) {
      result = '2';
    }
    var size = lengthBytesUTF8(result) + 1;
    var buffer = _malloc(size);
    stringToUTF8(result, buffer, size);
    return buffer;
  },

  // 1 when stored; 0 when the write failed (for example the quota is full).
  ManalensStorageSet: function (key, value) {
    try {
      window.localStorage.setItem(UTF8ToString(key), UTF8ToString(value));
      return 1;
    } catch (e) {
      return 0;
    }
  },

  // 1 when the key is gone.
  ManalensStorageRemove: function (key) {
    try {
      window.localStorage.removeItem(UTF8ToString(key));
      return 1;
    } catch (e) {
      return 0;
    }
  },
});
