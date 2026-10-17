// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Novabit
// NOVA Motion browser extension: hands the current video to the NOVA Motion app via a novamotion:// link.
// The app validates links (http/https only) before playing anything.

const MENU_PAGE = "novamotion-page";
const MENU_LINK = "novamotion-link";

function buildLink(url, seconds) {
  let link = "novamotion://play?url=" + encodeURIComponent(url);
  if (seconds && seconds > 1) link += "&t=" + Math.floor(seconds);
  return link;
}

// Runs inside the page: pause the playing video and report its position.
function grabPlayback() {
  const videos = [...document.querySelectorAll("video")];
  const v = videos.find((x) => !x.paused) || videos.sort((a, b) => b.clientWidth - a.clientWidth)[0];
  if (!v) return { time: 0 };
  const time = v.currentTime || 0;
  try { v.pause(); } catch (_) {}
  return { time };
}

async function sendTab(tab) {
  if (!tab || !/^https?:/.test(tab.url || "")) return;
  let time = 0;
  try {
    const [res] = await chrome.scripting.executeScript({ target: { tabId: tab.id }, func: grabPlayback });
    time = res?.result?.time || 0;
  } catch (_) {
    // pages we may not script (store pages etc.): send the URL without a position
  }
  // Navigating the tab to a custom scheme opens the registered app; the page itself stays.
  await chrome.tabs.update(tab.id, { url: buildLink(tab.url, time) });
}

chrome.action.onClicked.addListener(sendTab);

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: MENU_PAGE, title: "Smooth this video in NOVA Motion", contexts: ["page", "video"] });
  chrome.contextMenus.create({ id: MENU_LINK, title: "Play link in NOVA Motion", contexts: ["link"] });
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === MENU_LINK && /^https?:/.test(info.linkUrl || "")) {
    await chrome.tabs.update(tab.id, { url: buildLink(info.linkUrl, 0) });
  } else if (info.menuItemId === MENU_PAGE) {
    await sendTab(tab);
  }
});
