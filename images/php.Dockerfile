FROM php:8-cli AS final
RUN mv "$PHP_INI_DIR/php.ini-production" "$PHP_INI_DIR/php.ini"
COPY --from=mlocati/php-extension-installer /usr/bin/install-php-extensions /usr/local/bin/
RUN apt-get update && apt-get install -y wget git unzip && apt-get clean && rm -rf /var/cache/apt/lists
RUN install-php-extensions curl intl pcntl posix zip sodium opcache @composer && mkdir -p /app
WORKDIR /app
COPY composer.json composer.json
COPY composer.lock composer.lock
RUN composer install --no-dev -n -o
COPY reference reference
COPY tests tests
ENTRYPOINT ["php","-dopcache.enable_cli=1","-dopcache.jit_buffer_size=100M","tests/php/integration.php"]

FROM final AS unit
RUN composer install
ENTRYPOINT ["composer","test"]
