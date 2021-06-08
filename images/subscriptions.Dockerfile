FROM php:8-apache AS final
RUN mv "$PHP_INI_DIR/php.ini-production" "$PHP_INI_DIR/php.ini"
COPY --from=mlocati/php-extension-installer /usr/bin/install-php-extensions /usr/local/bin/
RUN apt-get update && apt-get install -y wget git unzip && apt-get clean && rm -rf /var/cache/apt/lists
RUN install-php-extensions curl intl pcntl posix zip sodium @composer && mkdir -p /app
WORKDIR /var/www/html
COPY composer.json composer.json
COPY composer.lock composer.lock
RUN composer install && a2enmod rewrite
COPY reference reference
COPY tests tests
RUN mv tests/subscriptions/service.php ./index.php && \
    mv tests/subscriptions/config.php ./config.php && \
    mv tests/subscriptions/.htaccess . && \
    mv vendor ../..
