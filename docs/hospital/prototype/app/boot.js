/* Runs synchronously before DOMContentLoaded, so the menu exists when scripts.bundle.js initialises
   KTMenu and the drawer. The first screen renders after the theme is up. */
(function () {
	'use strict';
	HMS.renderMenu();
	HMS.renderHeader();
	window.addEventListener('hashchange', HMS.render);
	document.addEventListener('DOMContentLoaded', function () { HMS.render(); });
})();
