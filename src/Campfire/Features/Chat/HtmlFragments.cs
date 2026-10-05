// Static markup translated from the pinned Rails/Go templates. Dynamic values are encoded by ChatRenderer.
namespace Campfire.Features.Chat;

internal static class HtmlFragments
{
    public const string Composer = """

  <div class="composer flex align-end gap position-relative"
      data-controller="typing-notifications" data-typing-notifications-active-class="typing-indicator--active">
    <a class="btn flex-item-no-shrink margin-block-end composer__context-btn" style="view-transition-name: input-switcher" href="/searches">
      <img aria-hidden="true" src="%%ASSET:search.svg%%" width="20" height="20" />
      <span class="for-screen-reader">Search</span>
</a>
    <turbo-frame id="composer-frame">
      <form id="composer" class="margin-block flex-item-grow contain" data-controller="composer drop-target" data-action="dragenter-&gt;drop-target#dragenter dragover-&gt;drop-target#dragover drop-&gt;drop-target#drop drop-target:drop@window-&gt;composer#dropFiles lexxy:file-accept-&gt;composer#preventAttachment refresh-room:online@window-&gt;composer#online typing-notifications#stop paste-&gt;composer#pasteFiles turbo:submit-end-&gt;composer#submitEnd refresh-room:offline@window-&gt;composer#offline" data-composer-messages-outlet="#message-area" data-composer-toolbar-class="composer--rich-text" data-composer-room-id-value="%%ROOM_ID%%" action="/rooms/%%ROOM_ID%%/messages" accept-charset="UTF-8" method="post">
        <fieldset data-composer-target="fields" contents>
          <div class="flex flex-column">
            <div class="composer__filelist flex flex--align-center gap flex-wrap" data-composer-target="fileList"></div>

            <div class="flex composer__input input input--actor fill-white min-width" style="--input-border-radius: 1.3rem">
              <div class="flex align-end gap full-width">
                <img aria-hidden="true" class="composer__input-hint colorize--black" style="view-transition-name: input-btn;" src="%%ASSET:messages-outlined.svg%%" width="22" height="22" />

                <div class="flex flex-column flex-item-grow min-width gap">
                  <lexxy-editor rows="1" class="input lexxy-content" style="order: -1" aria-multiline="true" aria-label="Write a message" permitted-attachment-types="application/vnd.campfire.mention application/vnd.actiontext.opengraph-embed" data-controller="unfurl" data-action="lexxy:change-&gt;typing-notifications#start keydown-&gt;composer#submitByKeyboard:capture lexxy:change-&gt;composer#saveDraft lexxy:insert-link-&gt;unfurl#unfurl" data-composer-target="text" data-direct-upload-url="/rails/active_storage/direct_uploads" data-blob-url-template="/rails/active_storage/blobs/redirect/:signed_id/:filename" id="message_body" input="message_body_trix_input_message" name="message[body]">
                    <lexxy-prompt trigger="@" name="mention" src="/autocompletable/users?room_id=%%ROOM_ID%%" remote-filtering="true" empty-results="No matches"></lexxy-prompt>
</lexxy-editor>                </div>

                <label class="btn btn--borderless txt-small flex-item-no-shrink composer__attachment-btn input--file">
                  <img class="colorize--black" aria-hidden="true" src="%%ASSET:attachment.svg%%" width="22" height="22" />
                  <input type="file" data-action="composer#filePicked" multiple />
                  <span class="for-screen-reader">Attach a file</span>
                </label>

                <button class="btn btn--borderless txt-small flex-item-no-shrink composer__rich-text-btn" type="button" data-action="composer#toggleToolbar">
                  <img class="colorize--black" aria-hidden="true" src="%%ASSET:text-options.svg%%" width="20" height="20" />
                  <span class="for-screen-reader">Rich text</span>
                </button>

                <button name="send" type="submit" data-action="composer#submit" class="btn btn--reversed flex-item-no-shrink txt-small">
                  <img aria-hidden="true" src="%%ASSET:arrow-up.svg%%" width="20" height="20" />
                  <span class="for-screen-reader">Send Message</span>
</button>              </div>
            </div>
          </div>
        </fieldset>

        <div class="typing-indicator gap txt-small align-center flex-inline" data-typing-notifications-target="indicator">
          <div class="typing-indicator__author spinner" data-typing-notifications-target="author"></div>
        </div>

        <input data-composer-target="clientid" type="hidden" name="message[client_message_id]" id="message_client_message_id" />
</form>    </turbo-frame>
  </div>

""";
    public const string Optimistic = """

<script type="text/template" data-messages-target="template">
  <div class="message message--me $messageClasses$"
      id="message_$clientMessageId$"
      data-format-message-target="message"
      data-user-id="%%USER_ID%%"
      data-message-timestamp="$messageTimestamp$"
      data-messages-target="message">
    <div class="message__day-separator"><time class="message__timestamp" datetime="$messageDatetime$" data-local-time-target="date"></time></div>

    <figure class="avatar message__avatar">
      <a title="%%USER_TITLE%%" class="btn avatar" data-turbo-frame="_top" href="/users/%%USER_ID%%"><img aria-hidden="true" src="%%USER_AVATAR%%" width="48" height="48" /></a>
    </figure>

    <div class="message__body">
      <div class="message__body-content">
        <div class="message__meta">
          <h3 class="message__heading">
            <span class="message__author"><strong>%%USER_NAME%%</strong></span>
            <span class="message__permalink"><time class="message__timestamp" datetime="$messageDatetime$" data-local-time-target="time"></time></span>
          </h3>
          <div class="message__actions">
            <div class="position-relative">
              <span class="btn message__action-btn message__options-btn">
                <img class="colorize--black" aria-hidden="true" src="%%ASSET:menu-dots-horizontal.svg%%" />
                <span class="for-screen-reader">Message options</span>
              </span>
            </div class="position-relative">
          </div>
        </div>
        $body$
      </div>
    </div>
  </div>
</script>

""";
    public const string Lightbox = """
<dialog class="lightbox" aria-label="Image Viewer (Press escape to close)" data-lightbox-target="dialog" data-action="close->lightbox#reset">
  <img src="" class="lightbox__image" data-lightbox-target="zoomedImage" />

  <form method="dialog" class="lightbox__btn">
    <button class="btn">
      <img aria-hidden="true" src="%%ASSET:remove.svg%%" />
      <span class="for-screen-reader">Close image viewer</span>
    </button>
  </form>

  <a href="" class="lightbox__btn--download btn hide-in-ios-pwa" data-lightbox-target="download">
    <img aria-hidden="true" src="%%ASSET:download.svg%%" />
    <span class="for-screen-reader">Download file</span>
  </a>

  <button class="lightbox__btn--share btn"
      data-controller="web-share"
      data-action="web-share#share"
      data-web-share-files-value=""
      data-lightbox-target="share">
    <img aria-hidden="true" src="%%ASSET:share.svg%%" />
    <span class="for-screen-reader">Share file</span>
  </button>
</dialog>


""";
    public const string DirectNew = """
<turbo-frame id="direct_rooms_control" target="_top"><div class="directs directs--new flex flex-column gap"><form class="flex gap flex-item-grow" data-controller="form" data-action="keydown.esc->form#cancel" action="/rooms/directs" accept-charset="UTF-8" method="post"><a class="btn flex-item-no-shrink" data-turbo-frame="user_sidebar" data-form-target="cancel" href="/users/me/sidebar"><img aria-hidden="true" src="%%ASSET:arrow-left.svg%%"><span class="for-screen-reader">Cancel changes</span></a><section class="autocomplete__container unpad input input--actor"><div class="autocomplete__input input flex flex-wrap position-relative flex-item-grow" data-controller="autocomplete" data-autocomplete-url-value="/autocompletable/users"><select name="user_ids[]" data-autocomplete-target="select" data-template-id="autocompletable-user" multiple hidden required></select><template id="autocompletable-user"><div class="autocomplete__pill max-width" data-value="" tabindex="0"><img class="avatar flex-item-no-shrink" data-content="avatar" src=""><span class="autocomplete-field__selected-value-text overflow-ellipsis flex-item-grow" data-content="label"></span><button type="button" data-action="autocomplete#remove:prevent" data-value="" tabindex="-1" class="btn btn--plain txt-small translucent flex-item-no-shrink"><img src="%%ASSET:remove-circle.svg%%" aria-hidden="true" class="colorize--black"><span class="for-screen-reader">Remove <span data-content="screenReaderLabel"></span></span></button></div></template><input autocomplete="off" autocorrect="off" data-1p-ignore="true" class="autocomplete__input input flex flex-wrap position-relative" data-autocomplete-target="input" data-action="input->autocomplete#search keydown->autocomplete#didPressKey" type="text" name="rooms_direct[user_ids_input]" id="rooms_direct_user_ids_input"></div></section><button name="button" type="submit" class="btn btn--reversed flex-item-no-shrink"><img aria-hidden="true" src="%%ASSET:check.svg%%"><span class="for-screen-reader">Start Ping</span></button></form><span class="txt-small translucent pad-inline-half center">Type names to ping someone…</span></div></turbo-frame>
""";
}
