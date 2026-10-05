# Run in the pinned Rails container: bin/rails runner /tmp/decrypt_cookies.rb
# Feed the ignored cookie-samples.json on stdin. No database writes.
samples = JSON.parse(STDIN.read)
key = Rails.application.key_generator.generate_key("authenticated encrypted cookie", 32)
encryptor = ActiveSupport::MessageEncryptor.new(key, cipher: "aes-256-gcm", serializer: :json)
result = samples.to_h do |name, sample|
  begin
    decoded = encryptor.decrypt_and_verify(sample.fetch("cookies").fetch("_campfire_session"), purpose: "cookie._campfire_session")
    stored = decoded.fetch("_csrf_token")
    raw = Base64.urlsafe_decode64(stored)
    supplied = Base64.urlsafe_decode64(sample.fetch("csrf_token"))
    unmasked = supplied.bytes.first(32).zip(supplied.bytes.last(32)).map { |a, b| a ^ b }.pack("C*")
    expected = OpenSSL::HMAC.digest("SHA256", raw, "!real_csrf_token")
    [name, { decrypted: true, session_keys: decoded.keys, csrf_matches: expected == unmasked }]
  rescue => error
    value = sample.fetch("cookies").fetch("_campfire_session")
    decoded_again = URI::DEFAULT_PARSER.unescape(value)
    extra_decode = begin
      session = encryptor.decrypt_and_verify(decoded_again, purpose: "cookie._campfire_session")
      { decrypted: true, session_keys: session.keys }
    rescue => second_error
      { decrypted: false, error: second_error.class.to_s, message: second_error.message }
    end
    [name, { decrypted: false, error: error.class.to_s, message: error.message, after_extra_uri_decode: extra_decode }]
  end
end
puts JSON.pretty_generate(result)
